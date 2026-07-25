using Renci.SshNet;
using Renci.SshNet.Common;

namespace MetaMCP.Host;

internal sealed class ReverseSshTunnel : IAsyncDisposable
{
    private readonly ReverseSshSettings _settings;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
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
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_sync)
        {
            _cts?.Cancel();
            loop = _loopTask;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = null;
            _loopTask = null;
        }

        SetState(_settings.Enabled ? ComponentState.Offline : ComponentState.Disabled, null);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(ComponentState.Starting, null);
                var endpoint = OpenSshConfig.Resolve(_settings);
                var methods = BuildAuthenticationMethods(endpoint);
                var connectionInfo = new ConnectionInfo(
                    endpoint.HostName,
                    endpoint.Port,
                    endpoint.User,
                    methods)
                {
                    Timeout = TimeSpan.FromSeconds(
                        Math.Clamp(_settings.ConnectTimeoutSeconds, 3, 120)),
                };

                using var client = new SshClient(connectionInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30),
                };

                client.HostKeyReceived += (_, eventArgs) =>
                {
                    eventArgs.CanTrust = IsHostKeyAccepted(eventArgs);
                };

                await Task.Run(client.Connect, cancellationToken);
                using var forward = new ForwardedPortRemote(
                    _settings.RemoteBindHost,
                    _settings.RemotePort,
                    _settings.LocalHost,
                    _settings.LocalPort);
                client.AddForwardedPort(forward);
                forward.Start();
                SetState(ComponentState.Online, null);

                while (!cancellationToken.IsCancellationRequested &&
                       client.IsConnected &&
                       forward.IsStarted)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }

                if (forward.IsStarted)
                {
                    forward.Stop();
                }

                if (client.IsConnected)
                {
                    client.Disconnect();
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
        }
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}