using System.Diagnostics;
using System.Text;

namespace MetaMCP.Host;

/// <summary>
/// Windows reverse SSH forwarding in a separate OpenSSH process. An SSH library
/// thread failure cannot terminate the WinForms bootstrapper.
/// </summary>
internal sealed class WindowsOpenSshTunnel : IAsyncDisposable
{
    private readonly ReverseSshSettings _settings;
    private readonly WindowsJob _job = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private Process? _currentProcess;
    private ComponentState _state;
    private string? _lastError;
    private string? _lastStderr;
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _lastOnline;
    private int _reconnects;

    public WindowsOpenSshTunnel(ReverseSshSettings settings)
    {
        _settings = settings;
        _state = settings.Enabled ? ComponentState.Offline : ComponentState.Disabled;
    }

    public ComponentState State { get { lock (_sync) return _state; } }
    public string? LastError { get { lock (_sync) return _lastError; } }

    public string DiagnosticSummary
    {
        get
        {
            lock (_sync)
            {
                return $"Transport: Windows OpenSSH (isolated process)\n" +
                    $"State: {_state}; reconnect attempts: {_reconnects}\n" +
                    $"Last attempt: {_lastAttempt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "none"}\n" +
                    $"Last online: {_lastOnline?.ToString("yyyy-MM-dd HH:mm:ss") ?? "none"}\n" +
                    $"Last failure: {_lastError ?? "none"}\n" +
                    $"SSH stderr: {_lastStderr ?? "none"}\n" +
                    $"Likely cause: {ClassifyFailure(_lastError, _lastStderr)}";
            }
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (!_settings.Enabled)
            {
                _state = ComponentState.Disabled;
                return;
            }
            if (_worker is { IsCompleted: false })
                return;
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;
            _worker = Task.Run(() => RunAsync(token));
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            worker = _worker;
            cts = _lifetime;
            cts?.Cancel();
            KillProcess(_currentProcess);
        }

        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException ex)
            {
                HostLog.Error("OpenSSH tunnel did not stop in 12 seconds.", ex);
                throw;
            }
        }
        lock (_sync)
        {
            if (ReferenceEquals(worker, _worker))
            {
                _worker = null;
                _lifetime = null;
                cts?.Dispose();
                _state = _settings.Enabled ? ComponentState.Offline : ComponentState.Disabled;
            }
        }
    }

    public async Task<ReverseSshResetResult> ResetRemoteForwardAsync(
        CancellationToken cancellationToken = default)
    {
        await StopAsync();
        Start();
        var mapping = _settings.GetActiveMapping();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        while (!timeout.IsCancellationRequested)
        {
            if (State == ComponentState.Online)
            {
                return new ReverseSshResetResult(
                    mapping.Id, mapping.DisplayName, mapping.RemotePort, 0, 0);
            }
            await Task.Delay(300, timeout.Token);
        }
        throw new TimeoutException($"Reverse SSH did not reconnect. {LastError}");
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var mapping = _settings.GetActiveMapping();
                var endpoint = OpenSshConfig.Resolve(_settings);
                if (!string.IsNullOrEmpty(_settings.Password) &&
                    string.IsNullOrEmpty(endpoint.IdentityFile))
                    throw new InvalidOperationException(
                        "Windows OpenSSH requires key-based authentication; password-only SSH settings are unsupported.");

                SetState(ComponentState.Starting, null);
                lock (_sync) _lastAttempt = DateTimeOffset.Now;
                var args = BuildArguments(endpoint, mapping, forwarding: true);
                process = StartSsh(args);
                _job.Assign(process); // KILL_ON_JOB_CLOSE also cleans up after a hard host crash.
                lock (_sync) _currentProcess = process;
                HostLog.Info($"Reverse SSH OpenSSH started PID={process.Id}; mapping={mapping.Id}; VPS port={mapping.RemotePort}.");

                var stderrTask = CaptureStderrAsync(process);
                var exitTask = process.WaitForExitAsync(token);
                await Task.Delay(1200, token);
                if (process.HasExited)
                    throw new IOException($"ssh.exe exited early ({process.ExitCode}). {await stderrTask}");

                await ProbeRemoteAsync(endpoint, mapping, token);
                SetState(ComponentState.Online, null);
                lock (_sync) _lastOnline = DateTimeOffset.Now;
                HostLog.Info($"Reverse SSH online via ssh.exe PID={process.Id}; remote port={mapping.RemotePort}.");

                int consecutiveFailedProbes = 0;
                while (!token.IsCancellationRequested && !process.HasExited)
                {
                    var interval = TimeSpan.FromSeconds(
                        Math.Clamp(_settings.HealthProbeIntervalSeconds, 5, 3600));
                    if (await Task.WhenAny(exitTask, Task.Delay(interval, token)) == exitTask)
                        break;

                    try
                    {
                        await ProbeRemoteAsync(endpoint, mapping, token);
                        consecutiveFailedProbes = 0;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch (Exception probeError)
                    {
                        consecutiveFailedProbes++;
                        HostLog.Warn($"Reverse SSH remote health probe {consecutiveFailedProbes}/2 failed: {probeError.Message}");
                        if (consecutiveFailedProbes >= 2) throw;
                    }
                }

                if (!token.IsCancellationRequested)
                    throw new IOException($"ssh.exe exited ({process.ExitCode}). {await stderrTask}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetState(ComponentState.Error, ex.Message);
                HostLog.Error("Reverse SSH OpenSSH attempt failed (will reconnect).", ex);
            }
            finally
            {
                KillProcess(process);
                if (process is not null)
                {
                    try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4)); }
                    catch (Exception) { }
                    process.Dispose();
                }
                lock (_sync) if (ReferenceEquals(_currentProcess, process)) _currentProcess = null;
            }

            if (!token.IsCancellationRequested)
            {
                lock (_sync) _reconnects++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(
                        Math.Clamp(_settings.ReconnectDelaySeconds, 2, 300)), token);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static Process StartSsh(IReadOnlyList<string> arguments)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "OpenSSH", "ssh.exe");
        if (!File.Exists(path))
            throw new FileNotFoundException("Windows OpenSSH Client is not installed.", path);

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };
        foreach (var value in arguments) info.ArgumentList.Add(value);
        return Process.Start(info) ?? throw new IOException("Failed to start ssh.exe.");
    }

    private List<string> BuildArguments(
        ResolvedSshEndpoint endpoint, ReverseSshMappingSettings mapping, bool forwarding)
    {
        var args = new List<string> {
            "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=" + Math.Clamp(_settings.ConnectTimeoutSeconds,3,120),
            "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            "-o", "ExitOnForwardFailure=yes", "-p", endpoint.Port.ToString(),
            "-l", endpoint.User,
        };
        if (!string.IsNullOrWhiteSpace(endpoint.IdentityFile))
        {
            args.Add("-i");
            args.Add(endpoint.IdentityFile);
        }
        if (forwarding)
        {
            args.Add("-N");
            args.Add("-R");
            args.Add($"{mapping.RemoteBindHost}:{mapping.RemotePort}:{mapping.LocalHost}:{mapping.LocalPort}");
        }
        args.Add(endpoint.HostName);
        return args;
    }

    private async Task ProbeRemoteAsync(
        ResolvedSshEndpoint endpoint, ReverseSshMappingSettings mapping, CancellationToken token)
    {
        var args = BuildArguments(endpoint, mapping, forwarding: false);
        var health = $"http://{mapping.RemoteBindHost}:{mapping.RemotePort}/metamcp/health";
        var seconds = Math.Clamp(_settings.HealthProbeTimeoutSeconds, 2, 30);
        args.Add($"curl --fail --silent --show-error --connect-timeout 3 --max-time {seconds} '{health}'");

        using var process = StartSsh(args);
        var stderr = process.StandardError.ReadToEndAsync();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds + 8));
        try { await process.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException($"VPS reverse-forward probe timed out for port {mapping.RemotePort}.");
        }
        if (process.ExitCode != 0)
        {
            var detail = (await stderr).Trim();
            throw new IOException(
                $"VPS reverse-forward probe failed (exit {process.ExitCode}): {detail}");
        }
    }

    private async Task<string> CaptureStderrAsync(Process process)
    {
        var last = new StringBuilder();
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (last.Length > 2048) last.Clear();
                last.AppendLine(line);
                lock (_sync) _lastStderr = line.Length > 512 ? line[..512] : line;
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn($"Could not capture ssh.exe stderr: {ex.Message}");
        }
        return last.ToString().Trim();
    }

    private void SetState(ComponentState state, string? error)
    {
        lock (_sync)
        {
            _state = state;
            if (error is not null || state == ComponentState.Online)
                _lastError = error;
        }
    }

    private static string ClassifyFailure(string? error, string? stderr)
    {
        var details = (error + " " + stderr).ToLowerInvariant();
        if (details.Contains("host identification has changed") || details.Contains("host key verification failed"))
            return "VPS SSH host key does not match trusted known_hosts; verify server identity before reconnecting.";
        if (details.Contains("permission denied") || details.Contains("authentication failed"))
            return "SSH key authentication failed; check the configured key and VPS account.";
        if (details.Contains("remote port forwarding failed") || details.Contains("address already in use") || details.Contains("cannot listen"))
            return "Remote VPS forwarding port is already occupied; check stale sshd listener on that port.";
        if (details.Contains("probe failed") || details.Contains("http code") || details.Contains("connection refused"))
            return "Tunnel target or remote HTTP health route is not responding; compare direct backend/gateway probes.";
        if (details.Contains("timed out") || details.Contains("network is unreachable"))
            return "Network/VPS/SSH connection timed out; check packet loss and remote sshd availability.";
        return string.IsNullOrWhiteSpace(error) ? "No active failure observed." : "Inspect SSH stderr and HTTP checks above.";
    }

    private static void KillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { _job.Dispose(); }
    }
}
