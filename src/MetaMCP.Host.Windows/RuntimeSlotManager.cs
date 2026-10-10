using System.Text.Json;

namespace MetaMCP.Host;

internal sealed class RuntimeSlotManager : IAsyncDisposable
{
    private sealed record SlotInstance(
        string Slot,
        string BaseDirectory,
        RuntimeTarget Target,
        RuntimeController Controller);

    private readonly string _root;
    private readonly HostSettings _settings;
    private readonly string _currentPath;
    private readonly string _pendingPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SlotInstance> _draining =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RuntimeGateway _gateway;
    private readonly WindowsOpenSshTunnel _tunnel;
    private SlotInstance _active;
    private bool _gatewayStarted;
    private bool _disposed;

    public RuntimeSlotManager(string root, HostSettings settings)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _settings = settings;
        _currentPath = Path.Combine(_root, "current.json");
        _pendingPath = Path.Combine(_root, "pending-update.json");

        var activeSlot = ReadActiveSlot();
        _active = CreateSlot(activeSlot);
        _tunnel = new WindowsOpenSshTunnel(settings.ReverseSsh);
        _gateway = new RuntimeGateway(
            settings.BackendPort,
            settings.FrontendPort,
            settings.HostControlToken,
            _active.Target,
            TimeSpan.FromSeconds(settings.RuntimeSessionIdleTimeoutSeconds),
            GetControlStatus,
            SwapFromControlAsync);
    }

    public RuntimeStatus CurrentStatus => WithStableServices(_active.Controller.CurrentStatus);
    public string ActiveSlot => _active.Slot;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_gatewayStarted)
            {
                await _gateway.StartAsync(cancellationToken);
                _gatewayStarted = true;
            }
            await _active.Controller.StartAsync(cancellationToken);
            if (_settings.ReverseSsh.Enabled)
            {
                _tunnel.Start();
            }
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
            await _tunnel.StopAsync();
            await _active.Controller.StopAsync(cancellationToken);
            foreach (var slot in _draining.Values.ToArray())
            {
                await slot.Controller.StopAsync(cancellationToken);
            }
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
            await _active.Controller.RestartAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeStatus> RefreshStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var status = await _active.Controller.RefreshStatusAsync(cancellationToken);
        if (status.DesiredRunning &&
            _settings.ReverseSsh.Enabled &&
            _tunnel.State != ComponentState.Online)
        {
            _tunnel.Start();
        }
        return WithStableServices(status);
    }

    private async Task<RuntimeStatus> RefreshActiveStatusCoreAsync(
        CancellationToken cancellationToken)
    {
        var status = await _active.Controller.RefreshStatusAsync(cancellationToken);
        return WithStableServices(status);
    }

    private RuntimeStatus WithStableServices(RuntimeStatus status) =>
        status with
        {
            ReverseSsh = _settings.ReverseSsh.Enabled
                ? _tunnel.State
                : ComponentState.Disabled,
            ReverseSshMappingId = _settings.ReverseSsh.ActiveMapping,
            LastError = status.LastError ?? _tunnel.LastError,
            UpdatedAt = DateTimeOffset.Now,
        };

    public async Task<string> GetConnectionDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var status = await RefreshStatusAsync(cancellationToken);
        var checks = await Task.WhenAll(
            ProbeHttpAsync($"http://127.0.0.1:{_settings.BackendPort}/metamcp/health/sessions", "Backend via gateway", cancellationToken),
            ProbeHttpAsync($"http://127.0.0.1:{_active.Controller.BackendPort}/health", "Backend direct", cancellationToken),
            ProbeHttpAsync($"http://127.0.0.1:{_settings.FrontendPort}/en", "Frontend via gateway", cancellationToken),
            ProbeHttpAsync($"http://127.0.0.1:{_active.Controller.FrontendPort}/en", "Frontend direct", cancellationToken));

        var backendDirectOk = checks[1].Contains("HTTP 200", StringComparison.Ordinal);
        var backendGatewayOk = checks[0].Contains("HTTP 200", StringComparison.Ordinal);
        var reason = !backendDirectOk
            ? "Backend does not respond directly: investigate Node.js CPU, logs, crashes or /health."
            : !backendGatewayOk
                ? "Backend is healthy directly, but gateway/session path is failing: investigate proxy and sessions."
                : status.ReverseSsh != ComponentState.Online
                    ? "Backend is healthy locally; investigate OpenSSH transport, VPS forwarded port and SSH stderr."
                    : "Local backend and SSH are healthy now; previous errors may be transient.";

        return string.Join(Environment.NewLine,
            "MetaMCP connection diagnostics — " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            "Active: Release" + _active.Slot + " | Host PID " + Environment.ProcessId,
            "Backend: " + status.Backend + " | Frontend: " + status.Frontend +
                " | PostgreSQL: " + status.Database + " | Reverse SSH: " + status.ReverseSsh,
            "",
            string.Join(Environment.NewLine, checks),
            "",
            _tunnel.DiagnosticSummary,
            "",
            "Runtime error: " + (status.LastError ?? "none"),
            "Assessment: " + reason);
    }

    private static async Task<string> ProbeHttpAsync(
        string url, string caption, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return $"{caption}: HTTP {(int)response.StatusCode} ({sw.ElapsedMilliseconds} ms)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"{caption}: FAILED ({sw.ElapsedMilliseconds} ms) {ex.GetType().Name}: {ex.Message}";
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
            if (!_settings.ReverseSsh.ActiveMapping.Equals(
                    mapping.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings.ReverseSsh.ActiveMapping = mapping.Id;
                _settings.Save(_root);
                await _tunnel.StopAsync();
                if (_active.Controller.DesiredRunning && _settings.ReverseSsh.Enabled)
                {
                    _tunnel.Start();
                }
            }

            return await RefreshActiveStatusCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ReverseSshResetResult> ResetReverseSshAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_active.Controller.DesiredRunning)
            {
                throw new InvalidOperationException(
                    "MetaMCP runtime must be running before resetting Reverse SSH.");
            }
            if (!_settings.ReverseSsh.Enabled)
            {
                throw new InvalidOperationException(
                    "Reverse SSH is disabled in host.json.");
            }

            var result = await _tunnel.ResetRemoteForwardAsync(cancellationToken);
            await RefreshActiveStatusCoreAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<object> SwapAsync(
        string? requestedSlot = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var pending = ReadPending();
            var targetSlot = NormalizeSlot(
                requestedSlot ?? pending?.CandidateSlot ?? Opposite(_active.Slot));
            if (targetSlot.Equals(_active.Slot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Runtime slot {targetSlot} is already active.");
            }
            if (_draining.ContainsKey(targetSlot))
            {
                throw new InvalidOperationException(
                    $"Runtime slot {targetSlot} is still draining existing MCP sessions.");
            }

            var targetBase = GetSlotBase(targetSlot);
            ValidateCandidate(targetBase);
            if (pending is not null &&
                !pending.CandidateSlot.Equals(targetSlot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"pending-update.json targets {pending.CandidateSlot}, not {targetSlot}.");
            }

            var candidate = CreateSlot(targetSlot);
            try
            {
                await candidate.Controller.StartAsync(cancellationToken);
                var candidateStatus = await candidate.Controller.RefreshStatusAsync(cancellationToken);
                if (candidateStatus.Backend != ComponentState.Online ||
                    candidateStatus.Frontend != ComponentState.Online)
                {
                    throw new InvalidOperationException(
                        $"Candidate {targetSlot} did not become healthy.");
                }
            }
            catch
            {
                await candidate.Controller.DisposeAsync();
                throw;
            }

            var previous = _active;
            _gateway.SwitchActive(candidate.Target);
            _active = candidate;
            _draining[previous.Slot] = previous;
            WriteCurrentState(previous, candidate, pending);
            TryDeletePending();
            _ = Task.Run(() => DrainAndRetireAsync(previous, _lifetime.Token));

            HostLog.Info(
                $"Runtime hot-swap committed: {previous.Slot} -> {candidate.Slot}; old MCP sessions remain pinned to {previous.Slot} until drained.");
            return GetControlStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<object> SwapFromControlAsync(
        string? slot,
        CancellationToken cancellationToken) =>
        await SwapAsync(slot, cancellationToken);

    private async Task DrainAndRetireAsync(
        SlotInstance previous,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                !_gateway.IsDrained(previous.Target))
            {
                await Task.Delay(1000, cancellationToken);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await previous.Controller.StopAsync(cancellationToken);
            await previous.Controller.DisposeAsync();
            _draining.TryRemove(previous.Slot, out _);
            HostLog.Info($"Drained runtime slot {previous.Slot} retired.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HostLog.Error($"Failed to retire drained runtime slot {previous.Slot}.", ex);
        }
    }

    private object GetControlStatus()
    {
        var active = _active;
        var draining = _draining.Values
            .Select(slot => _gateway.GetSlotSnapshot(slot.Target))
            .ToArray();
        return new
        {
            mode = "bootstrap-gateway",
            hostPid = Environment.ProcessId,
            hostExecutable = Application.ExecutablePath,
            activeSlot = active.Slot,
            activePath = active.BaseDirectory,
            active = _gateway.GetSlotSnapshot(active.Target),
            draining,
            reverseSsh = new
            {
                enabled = _settings.ReverseSsh.Enabled,
                state = _settings.ReverseSsh.Enabled
                    ? _tunnel.State.ToString()
                    : ComponentState.Disabled.ToString(),
                mapping = _settings.ReverseSsh.ActiveMapping,
            },
            pendingUpdate = File.Exists(_pendingPath),
            sessionIdleTimeoutSeconds = _settings.RuntimeSessionIdleTimeoutSeconds,
            updatedAt = DateTimeOffset.Now,
        };
    }

    private SlotInstance CreateSlot(string slot)
    {
        slot = NormalizeSlot(slot);
        var offset = slot == "A" ? 100 : 200;
        var target = new RuntimeTarget(
            slot,
            _settings.BackendPort + offset,
            _settings.FrontendPort + offset);
        var baseDirectory = GetSlotBase(slot);
        var controller = new RuntimeController(
            baseDirectory,
            _root,
            _settings,
            new WindowsRuntimePlatform(),
            target.BackendPort,
            target.FrontendPort,
            manageReverseSsh: false);
        return new SlotInstance(slot, baseDirectory, target, controller);
    }

    private string ReadActiveSlot()
    {
        if (File.Exists(_currentPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_currentPath));
                if (document.RootElement.TryGetProperty("activeSlot", out var active) &&
                    active.ValueKind == JsonValueKind.String)
                {
                    return NormalizeSlot(active.GetString()!);
                }
            }
            catch (Exception ex)
            {
                HostLog.Warn($"Could not read current.json, falling back to available slot: {ex.Message}");
            }
        }

        if (Directory.Exists(GetSlotBase("A"))) return "A";
        if (Directory.Exists(GetSlotBase("B"))) return "B";
        throw new DirectoryNotFoundException("Neither ReleaseA nor ReleaseB runtime slot exists.");
    }

    private PendingState? ReadPending()
    {
        if (!File.Exists(_pendingPath))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_pendingPath));
            var root = document.RootElement;
            return new PendingState(
                NormalizeSlot(root.GetProperty("candidateSlot").GetString()!),
                root.TryGetProperty("candidateBuiltAt", out var builtAt)
                    ? builtAt.GetString()
                    : null,
                root.TryGetProperty("sourceRepository", out var source)
                    ? source.GetString()
                    : null);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("pending-update.json is invalid.", ex);
        }
    }

    private void WriteCurrentState(
        SlotInstance previous,
        SlotInstance candidate,
        PendingState? pending)
    {
        var state = new
        {
            schemaVersion = 2,
            activeSlot = candidate.Slot,
            activePath = candidate.BaseDirectory,
            previousSlot = previous.Slot,
            previousPath = previous.BaseDirectory,
            hostPath = _root,
            hostExecutable = Application.ExecutablePath,
            activatedAt = DateTimeOffset.Now.ToString("o"),
            candidateBuiltAt = pending?.CandidateBuiltAt,
            sourceRepository = pending?.SourceRepository,
        };
        var temp = _currentPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, HostSettings.JsonOptions));
        File.Move(temp, _currentPath, overwrite: true);
    }

    private void ValidateCandidate(string baseDirectory)
    {
        foreach (var relative in new[]
        {
            @"runtime\node\node.exe",
            @"metamcp\backend\dist\index.js",
            @"metamcp\frontend\server.js",
        })
        {
            var path = Path.Combine(baseDirectory, relative);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Runtime candidate is incomplete.", path);
            }
        }
    }

    private string GetSlotBase(string slot) =>
        Path.Combine(_root, $"Release{NormalizeSlot(slot)}", "win-x64");

    private static string NormalizeSlot(string slot)
    {
        var normalized = slot.Trim().ToUpperInvariant();
        return normalized is "A" or "B"
            ? normalized
            : throw new ArgumentOutOfRangeException(nameof(slot), "Runtime slot must be A or B.");
    }

    private static string Opposite(string slot) =>
        NormalizeSlot(slot) == "A" ? "B" : "A";

    private void TryDeletePending()
    {
        try
        {
            if (File.Exists(_pendingPath))
            {
                File.Delete(_pendingPath);
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn($"Could not remove pending-update.json: {ex.Message}");
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
        _lifetime.Cancel();

        await _gate.WaitAsync();
        try
        {
            foreach (var slot in _draining.Values.ToArray())
            {
                try { await slot.Controller.DisposeAsync(); } catch { }
            }
            _draining.Clear();
            await _tunnel.StopAsync();
            await _active.Controller.DisposeAsync();
            await _gateway.DisposeAsync();
            await _tunnel.DisposeAsync();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }

    private sealed record PendingState(
        string CandidateSlot,
        string? CandidateBuiltAt,
        string? SourceRepository);
}
