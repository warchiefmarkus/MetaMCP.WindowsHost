using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MetaMCP.Host;

internal sealed class RuntimeController : IAsyncDisposable
{
    private readonly string _baseDirectory;
    private readonly HostSettings _settings;
    private readonly RuntimeLayout _layout;
    private readonly Dictionary<string, string> _environment;
    private readonly IRuntimePlatform _platform;
    private readonly ReverseSshTunnel _tunnel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusSync = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ProcessOutputBuffer _backendOutput = new();
    private readonly ProcessOutputBuffer _backendError = new();
    private readonly ProcessOutputBuffer _frontendOutput = new();
    private readonly ProcessOutputBuffer _frontendError = new();
    private Task _monitorTask;
    private Process? _backend;
    private Process? _frontend;
    private RuntimeStatus _status;
    private bool _desiredRunning;
    private bool _disposed;
    private int _restartInProgress;
    private int _backendUnhealthyChecks;
    private int _frontendUnhealthyChecks;
    private string? _lastError;

    public RuntimeController(string baseDirectory, HostSettings settings, IRuntimePlatform platform)
    {
        _baseDirectory = baseDirectory;
        _settings = settings;
        _platform = platform;
        _layout = new RuntimeLayout(baseDirectory, platform.NodeExecutableRelativePath);
        _environment = EnvFile.Load(_layout.EnvironmentFile);
        PrepareEnvironment();
        _tunnel = new ReverseSshTunnel(settings.ReverseSsh);
        _tunnel.StateChanged += () => _ = RefreshStatusAsync();
        _status = RuntimeStatus.Stopped(
            settings.ReverseSsh.Enabled,
            settings.ReverseSsh.ActiveMapping);
        _monitorTask = Task.Run(() => MonitorLoopAsync(_lifetime.Token));
    }

    public event Action<RuntimeStatus>? StatusChanged;

    public RuntimeStatus CurrentStatus
    {
        get
        {
            lock (_statusSync)
            {
                return _status;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_desiredRunning && IsAlive(_backend) && IsAlive(_frontend))
            {
                return;
            }

            _desiredRunning = true;
            _lastError = null;
            SetStartingStatus();
            await StopProcessesOnlyAsync();
            await StartCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _desiredRunning = _settings.AutoRestart;
            _lastError = BuildFailureMessage(ex);
            await StopCoreAsync(clearDesiredState: false);
            await RefreshStatusAsync();
            HostLog.Error("MetaMCP runtime startup failed.", ex);
            throw new InvalidOperationException(_lastError, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _desiredRunning = false;
            _lastError = null;
            await StopCoreAsync(clearDesiredState: true);
            await RefreshStatusAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _desiredRunning = true;
            _lastError = null;
            SetStartingStatus();
            await StopCoreAsync(clearDesiredState: false);
            await StartCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _desiredRunning = _settings.AutoRestart;
            _lastError = BuildFailureMessage(ex);
            await StopCoreAsync(clearDesiredState: false);
            await RefreshStatusAsync();
            HostLog.Error("MetaMCP runtime restart failed.", ex);
            throw new InvalidOperationException(_lastError, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeStatus> SwitchReverseSshMappingAsync(
        string mappingId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var mapping = _settings.ReverseSsh.GetMapping(mappingId);
            if (_settings.ReverseSsh.ActiveMapping.Equals(
                    mapping.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return await RefreshStatusAsync(cancellationToken);
            }

            _settings.ReverseSsh.ActiveMapping = mapping.Id;
            _settings.Save(_baseDirectory);

            await _tunnel.StopAsync();
            if (_desiredRunning && _settings.ReverseSsh.Enabled)
            {
                _tunnel.Start();
            }

            _lastError = null;
            return await RefreshStatusAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeStatus> RefreshStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(
            Math.Clamp(_settings.HealthCheckTimeoutMilliseconds, 250, 10000));

        var databaseTask = CheckTcpAsync(
            _settings.DatabaseHost,
            _settings.DatabasePort,
            timeout,
            cancellationToken);
        var backendTask = IsAlive(_backend)
            ? CheckHttpAsync(
                $"http://127.0.0.1:{_settings.BackendPort}/health",
                timeout,
                cancellationToken)
            : Task.FromResult(false);
        var frontendTask = IsAlive(_frontend)
            ? CheckHttpAsync(
                $"http://127.0.0.1:{_settings.FrontendPort}/en",
                timeout,
                cancellationToken)
            : Task.FromResult(false);

        await Task.WhenAll(databaseTask, backendTask, frontendTask);

        var status = new RuntimeStatus(
            _desiredRunning,
            await backendTask ? ComponentState.Online : ComponentState.Offline,
            await frontendTask ? ComponentState.Online : ComponentState.Offline,
            await databaseTask ? ComponentState.Online : ComponentState.Offline,
            _settings.ReverseSsh.Enabled ? _tunnel.State : ComponentState.Disabled,
            _settings.ReverseSsh.ActiveMapping,
            GetPid(_backend),
            GetPid(_frontend),
            _lastError ?? _tunnel.LastError,
            DateTimeOffset.Now);
        SetStatus(status);
        return status;
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        _layout.Validate();
        EnsurePortAvailable(_settings.BackendPort, "backend");
        EnsurePortAvailable(_settings.FrontendPort, "frontend");

        if (!await CheckTcpAsync(
                _settings.DatabaseHost,
                _settings.DatabasePort,
                TimeSpan.FromSeconds(5),
                cancellationToken))
        {
            throw new InvalidOperationException(
                $"PostgreSQL is unavailable at {_settings.DatabaseHost}:{_settings.DatabasePort}.");
        }

        if (_settings.RunMigrationsOnStart && File.Exists(_layout.MigrationEntry))
        {
            await RunMigrationAsync(cancellationToken);
        }

        _backend = StartNodeProcess(
            "backend",
            _layout.BackendEntry,
            _layout.BackendDirectory,
            _backendOutput,
            _backendError,
            new Dictionary<string, string>
            {
                ["BACKEND_HOST"] = "127.0.0.1",
                ["BACKEND_PORT"] = _settings.BackendPort.ToString(),
            });
        await WaitForReadyAsync(
            "Backend",
            _backend,
            $"http://127.0.0.1:{_settings.BackendPort}/health",
            _backendOutput,
            _backendError,
            cancellationToken);

        _frontend = StartNodeProcess(
            "frontend",
            _layout.FrontendEntry,
            _layout.FrontendDirectory,
            _frontendOutput,
            _frontendError,
            new Dictionary<string, string>
            {
                ["HOSTNAME"] = "127.0.0.1",
                ["PORT"] = _settings.FrontendPort.ToString(),
            });
        await WaitForReadyAsync(
            "Frontend",
            _frontend,
            $"http://127.0.0.1:{_settings.FrontendPort}/en",
            _frontendOutput,
            _frontendError,
            cancellationToken);

        _tunnel.Start();
        _lastError = null;
        ResetHealthFailureCounters();
        await RefreshStatusAsync(cancellationToken);
    }

    private Process StartNodeProcess(
        string name,
        string entryPoint,
        string workingDirectory,
        ProcessOutputBuffer stdout,
        ProcessOutputBuffer stderr,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        stdout.Clear();
        stderr.Clear();
        var startInfo = CreateNodeStartInfo(entryPoint, workingDirectory, extraEnvironment);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => stderr.Add(eventArgs.Data);
        process.Exited += (_, _) =>
        {
            HostLog.Info($"{name} exited with code {SafeExitCode(process)}.");
            _ = RefreshStatusAsync();
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Could not start {name}.");
        }

        try
        {
            _platform.AttachProcess(process);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        HostLog.Info($"Started {name}: PID {process.Id}.");
        return process;
    }

    private ProcessStartInfo CreateNodeStartInfo(
        string entryPoint,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var startInfo = new ProcessStartInfo(_layout.NodeExecutable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(entryPoint);

        foreach (var pair in _environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        if (extraEnvironment is not null)
        {
            foreach (var pair in extraEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    private async Task RunMigrationAsync(CancellationToken cancellationToken)
    {
        var stdout = new ProcessOutputBuffer(100);
        var stderr = new ProcessOutputBuffer(100);
        using var process = new Process
        {
            StartInfo = CreateNodeStartInfo(_layout.MigrationEntry, _layout.BackendDirectory),
        };
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => stderr.Add(eventArgs.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start database migration.");
        }

        _platform.AttachProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var details = FirstNonEmpty(stderr.ReadTail(), stdout.ReadTail());
            throw new InvalidOperationException(
                $"Database migration exited with code {process.ExitCode}.{FormatDetails(details)}");
        }
    }

    private async Task WaitForReadyAsync(
        string name,
        Process process,
        string url,
        ProcessOutputBuffer stdout,
        ProcessOutputBuffer stderr,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(
            Math.Clamp(_settings.StartupTimeoutSeconds, 5, 600));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var details = FirstNonEmpty(stderr.ReadTail(), stdout.ReadTail());
                throw new InvalidOperationException(
                    $"{name} exited before becoming ready with code {SafeExitCode(process)}."
                    + FormatDetails(details));
            }

            if (await CheckHttpAsync(
                    url,
                    TimeSpan.FromMilliseconds(1500),
                    cancellationToken,
                    acceptRedirects: true))
            {
                return;
            }

            await Task.Delay(400, cancellationToken);
        }

        var tail = FirstNonEmpty(stderr.ReadTail(), stdout.ReadTail());
        throw new TimeoutException(
            $"{name} did not become ready at {url}." + FormatDetails(tail));
    }

    private async Task StopCoreAsync(bool clearDesiredState)
    {
        await _tunnel.StopAsync();
        await StopProcessesOnlyAsync();
        if (clearDesiredState)
        {
            _desiredRunning = false;
        }
    }

    private async Task StopProcessesOnlyAsync()
    {
        var frontend = Interlocked.Exchange(ref _frontend, null);
        var backend = Interlocked.Exchange(ref _backend, null);
        await StopProcessAsync(frontend);
        await StopProcessAsync(backend);
    }

    private static async Task StopProcessAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
        }
        catch (Exception ex)
        {
            HostLog.Error($"Failed to stop PID {SafePid(process)}.", ex);
            TryKill(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(_settings.HealthCheckIntervalSeconds, 2, 3600)));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var status = await RefreshStatusAsync(cancellationToken);
                if (!_desiredRunning || !_settings.AutoRestart)
                {
                    ResetHealthFailureCounters();
                    continue;
                }

                var backendAlive = IsAlive(_backend);
                var frontendAlive = IsAlive(_frontend);
                _backendUnhealthyChecks = UpdateUnhealthyCount(
                    _backendUnhealthyChecks,
                    backendAlive && status.Backend == ComponentState.Online);
                _frontendUnhealthyChecks = UpdateUnhealthyCount(
                    _frontendUnhealthyChecks,
                    frontendAlive && status.Frontend == ComponentState.Online);

                var threshold = Math.Clamp(_settings.UnhealthyChecksBeforeRestart, 1, 30);
                if (!backendAlive ||
                    !frontendAlive ||
                    _backendUnhealthyChecks >= threshold ||
                    _frontendUnhealthyChecks >= threshold)
                {
                    _ = RestartAfterFailureAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RestartAfterFailureAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _restartInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_settings.RestartDelaySeconds, 2, 300)),
                cancellationToken);
            if (!_desiredRunning)
            {
                return;
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_desiredRunning)
                {
                    return;
                }

                SetStartingStatus();
                await StopCoreAsync(clearDesiredState: false);
                try
                {
                    await StartCoreAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _lastError = BuildFailureMessage(ex);
                    await StopCoreAsync(clearDesiredState: false);
                    await RefreshStatusAsync();
                    HostLog.Error("Automatic runtime recovery failed; another attempt will follow.", ex);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _restartInProgress, 0);
        }
    }

    private static int UpdateUnhealthyCount(int current, bool healthy) =>
        healthy ? 0 : Math.Min(current + 1, int.MaxValue);

    private void ResetHealthFailureCounters()
    {
        _backendUnhealthyChecks = 0;
        _frontendUnhealthyChecks = 0;
    }

    private void PrepareEnvironment()
    {
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var nodeDirectory = Path.GetDirectoryName(_layout.NodeExecutable)!;
        _environment["PATH"] = nodeDirectory + Path.PathSeparator + existingPath;
        _environment["NODE_ENV"] = "production";
        _environment["BACKEND_HOST"] = "127.0.0.1";
        _environment["METAMCP_NPX_CWD"] = _layout.RunnerDirectory;
    }

    private void SetStartingStatus()
    {
        SetStatus(new RuntimeStatus(
            true,
            ComponentState.Starting,
            ComponentState.Starting,
            ComponentState.Starting,
            _settings.ReverseSsh.Enabled ? ComponentState.Starting : ComponentState.Disabled,
            _settings.ReverseSsh.ActiveMapping,
            GetPid(_backend),
            GetPid(_frontend),
            null,
            DateTimeOffset.Now));
    }

    private void SetStatus(RuntimeStatus status)
    {
        lock (_statusSync)
        {
            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    private void EnsurePortAvailable(int port, string name)
    {
        var listener = new TcpListener(IPAddress.Loopback, port)
        {
            ExclusiveAddressUse = true,
        };
        try
        {
            listener.Start();
        }
        catch (SocketException initialError)
        {
            if (!_platform.TryCleanupOrphanedNodeProcess(port, name))
            {
                throw new InvalidOperationException(
                    $"Port {port} required by {name} is occupied.",
                    initialError);
            }

            try
            {
                listener.Start();
            }
            catch (SocketException retryError)
            {
                throw new InvalidOperationException(
                    $"Port {port} required by {name} is still occupied after cleanup.",
                    retryError);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<bool> CheckHttpAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool acceptRedirects = false)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            return acceptRedirects
                ? (int)response.StatusCode < 500
                : response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckTcpAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, timeoutSource.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string BuildFailureMessage(Exception exception)
    {
        var details = FirstNonEmpty(
            _frontendError.ReadTail(),
            _backendError.ReadTail(),
            _frontendOutput.ReadTail(),
            _backendOutput.ReadTail());
        return exception.Message + FormatDetails(details);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatDetails(string details) =>
        string.IsNullOrWhiteSpace(details)
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}{details}";

    private static bool IsAlive(Process? process)
    {
        try { return process is not null && !process.HasExited; }
        catch { return false; }
    }

    private static int? GetPid(Process? process) => IsAlive(process) ? process!.Id : null;
    private static int SafePid(Process process) { try { return process.Id; } catch { return -1; } }
    private static int SafeExitCode(Process process) { try { return process.ExitCode; } catch { return -1; } }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _desiredRunning = false;
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync(clearDesiredState: true);
        }
        finally
        {
            _gate.Release();
        }

        try { await _monitorTask; } catch (OperationCanceledException) { }
        await _tunnel.DisposeAsync();
        _http.Dispose();
        _platform.Dispose();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
