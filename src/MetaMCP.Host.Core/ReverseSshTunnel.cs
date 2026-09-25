using Renci.SshNet;
using Renci.SshNet.Common;
using System.Text.RegularExpressions;

namespace MetaMCP.Host;

internal sealed record ReverseSshResetResult(
    string MappingId,
    string DisplayName,
    uint RemotePort,
    int FoundSessionCount,
    int TerminatedSessionCount)
{
    public string Summary =>
        $"{DisplayName}: cleared {TerminatedSessionCount}/{FoundSessionCount} " +
        $"stale SSH session(s) on VPS port {RemotePort}.";
}

internal sealed class ReverseSshTunnel : IAsyncDisposable
{
    private static readonly Regex SshProcessIdPattern = new(
        @"\bpid=(\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ReverseSshSettings _settings;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ActiveConnection? _activeConnection;
    private bool _stopping;
    private ComponentState _state;
    private string? _lastError;

    public ReverseSshTunnel(ReverseSshSettings settings)
    {
        _settings = settings;
        _state = settings.Enabled ? ComponentState.Offline : ComponentState.Disabled;
    }

    public event Action? StateChanged;

    public ComponentState State
    {
        get { lock (_sync) return _state; }
    }

    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    public void Start()
    {
        if (!_settings.Enabled)
        {
            SetState(ComponentState.Disabled, null);
            return;
        }

        lock (_sync)
        {
            if (_stopping ||
                _activeConnection is not null ||
                _loopTask is { IsCompleted: false })
            {
                return;
            }

            if (_loopTask is not null)
            {
                _loopTask = null;
                _cts?.Dispose();
                _cts = null;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => RunLoopAsync(cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        ActiveConnection? activeConnection;
        lock (_sync)
        {
            _stopping = true;
            cts = _cts;
            loop = _loopTask;
            activeConnection = _activeConnection;
            cts?.Cancel();
        }

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            if (activeConnection is not null)
            {
                await activeConnection.ShutdownAsync().WaitAsync(
                    GetRemainingStopTimeout(deadline));
            }

            if (loop is not null)
            {
                await loop.WaitAsync(GetRemainingStopTimeout(deadline));
            }

            lock (_sync)
            {
                if (ReferenceEquals(_loopTask, loop) &&
                    (loop is null || loop.IsCompleted))
                {
                    _loopTask = null;
                    if (ReferenceEquals(_cts, cts))
                    {
                        _cts = null;
                    }
                }

                if (ReferenceEquals(_activeConnection, activeConnection))
                {
                    _activeConnection = null;
                }
            }

            cts?.Dispose();
            SetState(
                _settings.Enabled ? ComponentState.Offline : ComponentState.Disabled,
                null);
        }
        catch (TimeoutException ex)
        {
            HostLog.Error(
                "Reverse SSH tunnel did not stop within the shutdown timeout. " +
                "Keeping its task state to prevent a duplicate tunnel.",
                ex);
            throw new TimeoutException(
                "Reverse SSH tunnel did not stop within 15 seconds; " +
                "a duplicate tunnel was not started.",
                ex);
        }
        finally
        {
            lock (_sync)
            {
                _stopping = false;
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ActiveConnection? connection = null;
            ReverseSshMappingSettings? mapping = null;
            ResolvedSshEndpoint? endpoint = null;
            try
            {
                SetState(ComponentState.Starting, null);
                mapping = _settings.GetActiveMapping();
                endpoint = OpenSshConfig.Resolve(_settings);
                var client = CreateClient(endpoint);
                connection = new ActiveConnection(client);
                SetActiveConnection(connection);
                await ConnectAsync(client, cancellationToken);
                var forward = new ForwardedPortRemote(
                    mapping.RemoteBindHost,
                    mapping.RemotePort,
                    mapping.LocalHost,
                    mapping.LocalPort);
                connection.AttachForward(forward);
                Exception? clientError = null;
                Exception? forwardError = null;
                client.ErrorOccurred += (_, eventArgs) =>
                    Interlocked.CompareExchange(
                        ref clientError,
                        eventArgs.Exception,
                        null);
                forward.Exception += (_, eventArgs) =>
                    Interlocked.CompareExchange(
                        ref forwardError,
                        eventArgs.Exception,
                        null);
                client.AddForwardedPort(forward);
                forward.Start();
                await ProbeConnectionAsync(client, mapping, cancellationToken);
                SetState(ComponentState.Online, null);

                var probeInterval = TimeSpan.FromSeconds(Math.Clamp(
                    _settings.HealthProbeIntervalSeconds,
                    5,
                    3600));
                var nextProbe = DateTime.UtcNow + probeInterval;
                while (!cancellationToken.IsCancellationRequested &&
                       client.IsConnected &&
                       forward.IsStarted)
                {
                    ThrowIfTunnelError(clientError, forwardError);

                    var remaining = nextProbe - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(
                            remaining > TimeSpan.FromSeconds(2)
                                ? TimeSpan.FromSeconds(2)
                                : remaining,
                            cancellationToken);
                        continue;
                    }

                    await ProbeConnectionAsync(client, mapping, cancellationToken);
                    nextProbe = DateTime.UtcNow + probeInterval;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new SshConnectionException("SSH connection closed.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(ComponentState.Error, ex.Message);
                HostLog.Error("Reverse SSH tunnel failed.", ex);
                if (mapping is not null &&
                    endpoint is not null &&
                    IsRemoteBindConflict(ex))
                {
                    try
                    {
                        if (connection is not null)
                        {
                            await connection.ShutdownAsync();
                        }

                        await TryReclaimRemoteListenerAsync(
                            mapping,
                            endpoint,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception cleanupError)
                    {
                        HostLog.Error(
                            $"Could not reclaim stale SSH listener on VPS port " +
                            $"{mapping.RemotePort}.",
                            cleanupError);
                    }
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Clamp(_settings.ReconnectDelaySeconds, 2, 300)),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (connection is not null)
                {
                    try
                    {
                        await connection.ShutdownAsync();
                    }
                    catch (Exception cleanupError)
                    {
                        HostLog.Error(
                            "Failed to clean up Reverse SSH connection resources.",
                            cleanupError);
                    }
                    finally
                    {
                        ClearActiveConnection(connection);
                    }
                }
            }
        }
    }

    public async Task<ReverseSshResetResult> ResetRemoteForwardAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            throw new InvalidOperationException("Reverse SSH is disabled in host.json.");
        }

        var mapping = _settings.GetActiveMapping();
        var endpoint = OpenSshConfig.Resolve(_settings);
        if (mapping.RemotePort == (uint)endpoint.Port)
        {
            throw new InvalidOperationException(
                $"Refusing to reset VPS port {mapping.RemotePort}: it is also " +
                $"the SSH control port for {endpoint.HostName}.");
        }

        await StopAsync();

        ReverseSshResetResult result;
        try
        {
            using var client = CreateClient(endpoint);
            await ConnectAsync(client, cancellationToken);

            var initialPids = GetRemoteSshListenerPids(client, mapping.RemotePort);
            var targetPids = initialPids.ToHashSet();
            var terminatedCount = 0;

            if (targetPids.Count > 0)
            {
                ExecuteRemoteCommand(client, BuildKillCommand(targetPids, force: false));
                var remainingPids = await WaitForRemotePidsToExitAsync(
                    client,
                    mapping.RemotePort,
                    targetPids,
                    cancellationToken);

                if (remainingPids.Count > 0)
                {
                    ExecuteRemoteCommand(client, BuildKillCommand(remainingPids, force: true));
                    remainingPids = await WaitForRemotePidsToExitAsync(
                        client,
                        mapping.RemotePort,
                        remainingPids.ToHashSet(),
                        cancellationToken);
                }

                if (remainingPids.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Remote SSH listener process(es) did not exit on VPS port " +
                        $"{mapping.RemotePort}: {string.Join(", ", remainingPids)}.");
                }

                terminatedCount = targetPids.Count;
            }

            var otherPids = GetRemoteSshListenerPids(client, mapping.RemotePort)
                .Where(pid => !targetPids.Contains(pid))
                .ToArray();
            if (otherPids.Length > 0)
            {
                throw new InvalidOperationException(
                    $"VPS port {mapping.RemotePort} is still occupied by another " +
                    $"SSH listener process: {string.Join(", ", otherPids)}.");
            }

            result = new ReverseSshResetResult(
                mapping.Id,
                mapping.DisplayName,
                mapping.RemotePort,
                targetPids.Count,
                terminatedCount);
        }
        finally
        {
            Start();
        }

        await WaitForOnlineAsync(cancellationToken);
        return result;
    }

    private AuthenticationMethod[] BuildAuthenticationMethods(ResolvedSshEndpoint endpoint)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(endpoint.IdentityFile))
        {
            if (!File.Exists(endpoint.IdentityFile))
            {
                throw new FileNotFoundException("SSH private key was not found.", endpoint.IdentityFile);
            }

            var key = string.IsNullOrEmpty(_settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(endpoint.IdentityFile)
                : new PrivateKeyFile(endpoint.IdentityFile, _settings.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(endpoint.User, key));
        }

        if (!string.IsNullOrEmpty(_settings.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(endpoint.User, _settings.Password));
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException(
                "No SSH authentication method is configured. Set PrivateKeyPath or Password.");
        }

        return methods.ToArray();
    }

    private SshClient CreateClient(ResolvedSshEndpoint endpoint)
    {
        var connectionInfo = new ConnectionInfo(
            endpoint.HostName,
            endpoint.Port,
            endpoint.User,
            BuildAuthenticationMethods(endpoint))
        {
            Timeout = TimeSpan.FromSeconds(
                Math.Clamp(_settings.ConnectTimeoutSeconds, 3, 120)),
        };

        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        client.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = IsHostKeyAccepted(eventArgs);
        };
        return client;
    }

    private void SetActiveConnection(ActiveConnection connection)
    {
        lock (_sync)
        {
            _activeConnection = connection;
        }
    }

    private void ClearActiveConnection(ActiveConnection connection)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeConnection, connection))
            {
                _activeConnection = null;
            }
        }
    }

    private static TimeSpan GetRemainingStopTimeout(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.Zero;
    }

    private static async Task ConnectAsync(
        SshClient client,
        CancellationToken cancellationToken)
    {
        await client.ConnectAsync(cancellationToken);
    }

    private async Task ProbeConnectionAsync(
        SshClient client,
        ReverseSshMappingSettings mapping,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Clamp(
            _settings.HealthProbeTimeoutSeconds,
            1,
            60);
        var healthUrl = BuildRemoteHealthUrl(mapping);
        var commandText =
            $"curl --fail --silent --show-error " +
            $"--connect-timeout {Math.Min(timeoutSeconds, 3)} " +
            $"--max-time {timeoutSeconds} {ShellQuote(healthUrl)}";
        using var command = client.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds + 2);
        await command.ExecuteAsync(cancellationToken);
        var result = command.Result;
        if (command.ExitStatus != 0)
        {
            var details = string.IsNullOrWhiteSpace(command.Error)
                ? result.Trim()
                : command.Error.Trim();
            throw new SshConnectionException(
                $"Reverse SSH data-plane probe failed for {healthUrl}: {details}");
        }
    }

    private static string BuildRemoteHealthUrl(ReverseSshMappingSettings mapping)
    {
        var host = mapping.RemoteBindHost;
        if (host.Contains(':') &&
            !host.StartsWith("[", StringComparison.Ordinal))
        {
            host = $"[{host}]";
        }

        // The reverse forward terminates at the local MetaMCP frontend. The
        // VPS public path may be rewritten by nginx, so probe the local app's
        // canonical health endpoint instead of the external VPS path.
        return $"http://{host}:{mapping.RemotePort}/metamcp/health";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static bool IsRemoteBindConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("already in use", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cannot bind", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("failed to bind", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task TryReclaimRemoteListenerAsync(
        ReverseSshMappingSettings mapping,
        ResolvedSshEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (mapping.RemotePort == (uint)endpoint.Port)
        {
            HostLog.Error(
                $"Refusing automatic stale-listener cleanup on VPS port " +
                $"{mapping.RemotePort}: it is also the SSH control port.",
                new InvalidOperationException("Remote forward/control port collision."));
            return;
        }

        using var client = CreateClient(endpoint);
        await ConnectAsync(client, cancellationToken);

        var targetPids = GetRemoteSshListenerPids(client, mapping.RemotePort)
            .ToHashSet();
        if (targetPids.Count == 0)
        {
            HostLog.Info(
                $"Reverse SSH bind conflict reported for VPS port {mapping.RemotePort}, " +
                "but no sshd listener was found to reclaim.");
            return;
        }

        ExecuteRemoteCommand(client, BuildKillCommand(targetPids, force: false));
        var remainingPids = await WaitForRemotePidsToExitAsync(
            client,
            mapping.RemotePort,
            targetPids,
            cancellationToken);
        if (remainingPids.Count > 0)
        {
            ExecuteRemoteCommand(client, BuildKillCommand(remainingPids, force: true));
            remainingPids = await WaitForRemotePidsToExitAsync(
                client,
                mapping.RemotePort,
                remainingPids.ToHashSet(),
                cancellationToken);
        }

        if (remainingPids.Count > 0)
        {
            throw new InvalidOperationException(
                $"Remote SSH listener process(es) did not exit on VPS port " +
                $"{mapping.RemotePort}: {string.Join(", ", remainingPids)}.");
        }

        HostLog.Info(
            $"Automatically reclaimed {targetPids.Count} stale SSH listener(s) " +
            $"on VPS port {mapping.RemotePort}.");
    }

    private static void ThrowIfTunnelError(
        Exception? clientError,
        Exception? forwardError)
    {
        if (forwardError is not null)
        {
            throw new SshConnectionException(
                "Reverse SSH forwarding failed.",
                forwardError);
        }

        if (clientError is not null)
        {
            throw new SshConnectionException(
                "SSH client reported a connection error.",
                clientError);
        }
    }

    private static string ExecuteRemoteCommand(SshClient client, string commandText)
    {
        using var command = client.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(10);
        var result = command.Execute();
        if (command.ExitStatus != 0)
        {
            var details = string.IsNullOrWhiteSpace(command.Error)
                ? result.Trim()
                : command.Error.Trim();
            throw new InvalidOperationException(
                $"Remote command failed with exit code {command.ExitStatus}: {details}");
        }

        return result;
    }

    private static IReadOnlyList<int> GetRemoteSshListenerPids(
        SshClient client,
        uint remotePort)
    {
        var output = ExecuteRemoteCommand(
            client,
            "sudo -n ss -ltnp 2>/dev/null");
        var pids = new HashSet<int>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("sshd", StringComparison.OrdinalIgnoreCase) ||
                !ContainsPort(line, remotePort))
            {
                continue;
            }

            foreach (Match match in SshProcessIdPattern.Matches(line))
            {
                if (int.TryParse(match.Groups[1].Value, out var pid) && pid > 1)
                {
                    pids.Add(pid);
                }
            }
        }

        return pids.OrderBy(pid => pid).ToArray();
    }

    private static bool ContainsPort(string line, uint port)
    {
        var token = $":{port}";
        var offset = 0;
        while (true)
        {
            var index = line.IndexOf(token, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var after = index + token.Length;
            var afterIsDigit = after < line.Length && char.IsDigit(line[after]);
            if (!afterIsDigit)
            {
                return true;
            }

            offset = after;
        }
    }

    private static string BuildKillCommand(
        IEnumerable<int> pids,
        bool force)
    {
        var signal = force ? "-KILL" : "-TERM";
        var pidList = string.Join(' ', pids.OrderBy(pid => pid));
        return $"sudo -n kill {signal} {pidList}";
    }

    private async Task<IReadOnlyList<int>> WaitForRemotePidsToExitAsync(
        SshClient client,
        uint remotePort,
        IReadOnlySet<int> targetPids,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            var currentPids = GetRemoteSshListenerPids(client, remotePort);
            var remaining = currentPids
                .Where(targetPids.Contains)
                .ToArray();
            if (remaining.Length == 0 || DateTime.UtcNow >= deadline)
            {
                return remaining;
            }

            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task WaitForOnlineAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            _settings.ConnectTimeoutSeconds + _settings.ReconnectDelaySeconds + 10,
            10,
            120));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (State != ComponentState.Online)
        {
            await Task.Delay(250, timeoutSource.Token);
        }
    }

    private bool IsHostKeyAccepted(HostKeyEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(_settings.HostKeyFingerprint))
        {
            return true;
        }

        var expected = NormalizeFingerprint(_settings.HostKeyFingerprint);
        var actual = Convert.ToHexString(eventArgs.FingerPrint);
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFingerprint(string value) =>
        value.Replace("SHA256:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(":", string.Empty)
            .Replace("-", string.Empty)
            .Trim();

    private void SetState(ComponentState state, string? error)
    {
        lock (_sync)
        {
            _state = state;
            _lastError = error;
        }

        StateChanged?.Invoke();
    }

    private sealed class ActiveConnection
    {
        private readonly object _sync = new();
        private ForwardedPortRemote? _forward;
        private Task? _shutdownTask;
        private bool _shutdownRequested;

        public ActiveConnection(SshClient client)
        {
            Client = client;
        }

        public SshClient Client { get; }

        public void AttachForward(ForwardedPortRemote forward)
        {
            var shutdownRequested = false;
            lock (_sync)
            {
                _forward = forward;
                shutdownRequested = _shutdownRequested;
            }

            if (shutdownRequested)
            {
                DisposeLateForward(forward);
            }
        }

        public Task ShutdownAsync()
        {
            lock (_sync)
            {
                _shutdownRequested = true;
                return _shutdownTask ??= Task.Run(ShutdownCore);
            }
        }

        private void ShutdownCore()
        {
            ForwardedPortRemote? forward;
            lock (_sync)
            {
                forward = _forward;
            }

            try
            {
                if (forward?.IsStarted == true)
                {
                    forward.Stop();
                }
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to stop Reverse SSH forwarding.", ex);
            }

            try
            {
                if (Client.IsConnected)
                {
                    Client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to disconnect Reverse SSH client.", ex);
            }

            try
            {
                forward?.Dispose();
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to dispose Reverse SSH forwarding.", ex);
            }

            try
            {
                Client.Dispose();
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to dispose Reverse SSH client.", ex);
            }
        }

        private static void DisposeLateForward(ForwardedPortRemote forward)
        {
            try
            {
                if (forward.IsStarted)
                {
                    forward.Stop();
                }
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to stop a late Reverse SSH forward.", ex);
            }

            try
            {
                forward.Dispose();
            }
            catch (Exception ex)
            {
                HostLog.Error("Failed to dispose a late Reverse SSH forward.", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
