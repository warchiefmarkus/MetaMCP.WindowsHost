using System.Diagnostics;
using Microsoft.Win32;

namespace MetaMCP.Host;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private sealed record McpConnectionInfo(
        string ServerName,
        string ServerType,
        string Kind,
        int? ProcessId,
        string[] SessionIds,
        int InFlight);

    private sealed record McpSessionInfo(
        string SessionId,
        int InFlightOperations,
        int OpenEventStreams,
        long IdleMilliseconds);

    private readonly string _baseDirectory;
    private HostSettings _settings;
    private RuntimeController? _portableRuntime;
    private bool _serviceMode;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _overallItem;
    private readonly ToolStripMenuItem _backendItem;
    private readonly ToolStripMenuItem _frontendItem;
    private readonly ToolStripMenuItem _databaseItem;
    private readonly ToolStripMenuItem _sshItem;
    private readonly ToolStripMenuItem _sessionsItem;
    private readonly ToolStripMenuItem _mappingItem;
    private readonly Dictionary<string, ToolStripMenuItem> _mappingItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _resetMcpConnectionsItem;
    private readonly ToolStripMenuItem _installServiceItem;
    private readonly ToolStripMenuItem _uninstallServiceItem;
    private readonly ToolStripMenuItem _openMetaMcpItem;
    private readonly ToolStripMenuItem _openConfigItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _mcpMetricsTimer;
    private readonly HttpClient _metricsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly McpProcessMetricsSampler _mcpMetricsSampler = new();
    private readonly Image _greenDot = CreateDot(Color.LimeGreen);
    private readonly Image _yellowDot = CreateDot(Color.Goldenrod);
    private readonly Image _redDot = CreateDot(Color.Crimson);
    private readonly Image _grayDot = CreateDot(Color.Gray);
    private readonly Image _appMenuIcon;
    private Image _jsonIcon;
    private bool _busy;
    private bool _exiting;
    private bool _backendOnline;
    private bool _mcpMetricsRefreshInProgress;
    private OverallState _lastOverallState = OverallState.Offline;
    private McpProcessMetrics? _lastMcpMetrics;
    private int? _lastSessionCount;
    private int? _lastConnectionCount;
    private DateTimeOffset? _lastMcpTelemetryAt;

    public TrayApplicationContext(string? baseDirectory = null)
    {
        _baseDirectory = (baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _settings = HostSettings.Load(_baseDirectory);
        HostLog.Initialize(_baseDirectory, _settings.LoggingEnabled);
        _serviceMode = ServiceInstaller.IsInstalled();

        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _appMenuIcon = LoadEmbeddedIcon("metamcp_32.png") ?? _applicationIcon.ToBitmap();
        _jsonIcon = CreateJsonForCurrentTheme();
        _menu = new ContextMenuStrip();
        _modeItem = CreateInformationItem(string.Empty);
        _overallItem = CreateStatusItem("Status: checking...");
        _backendItem = CreateStatusItem("Backend: checking...");
        _frontendItem = CreateStatusItem("Frontend: checking...");
        _databaseItem = CreateStatusItem("PostgreSQL: checking...");
        _sshItem = CreateStatusItem("Reverse SSH: checking...");
        _sessionsItem = CreateStatusItem("Sessions: checking...");
        _mappingItem = new ToolStripMenuItem("Reverse SSH mapping");
        BuildMappingMenu();
        _menu.Items.AddRange([
            _modeItem,
            new ToolStripSeparator(),
            _overallItem,
            _backendItem,
            _frontendItem,
            _databaseItem,
            _sshItem,
            new ToolStripSeparator(),
            _sessionsItem,
            new ToolStripSeparator(),
            _mappingItem,
            new ToolStripSeparator(),
        ]);

        _openMetaMcpItem = new ToolStripMenuItem("Open MetaMCP", _appMenuIcon, (_, _) => OpenFrontend());
        _openConfigItem = new ToolStripMenuItem("Open configuration", _jsonIcon, (_, _) => OpenConfiguration());
        _menu.Items.Add(_openMetaMcpItem);
        _menu.Items.Add(_openConfigItem);
        _menu.Items.Add(new ToolStripSeparator());
        _startItem = new ToolStripMenuItem("Start", null, async (_, _) => await StartRuntimeAsync());
        _stopItem = new ToolStripMenuItem("Stop", null, async (_, _) => await StopRuntimeAsync());
        _restartItem = new ToolStripMenuItem("Restart", null, async (_, _) => await RestartRuntimeAsync());
        _resetMcpConnectionsItem = new ToolStripMenuItem(
            "Reset MCP connections",
            null,
            async (_, _) => await ResetMcpConnectionsAsync())
        {
            ToolTipText = "Close all downstream MCP servers without stopping MetaMCP.",
        };
        _menu.Items.AddRange([_startItem, _stopItem, _restartItem]);
        _menu.Items.Add(_resetMcpConnectionsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _installServiceItem = new ToolStripMenuItem(
            "Install Windows Service",
            null,
            async (_, _) => await InstallServiceAsync());
        _uninstallServiceItem = new ToolStripMenuItem(
            "Uninstall Windows Service",
            null,
            async (_, _) => await UninstallServiceAsync());
        _menu.Items.AddRange([_installServiceItem, _uninstallServiceItem]);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "MetaMCP starting...",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenFrontend();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _menu.Show(Cursor.Position);
            }
        };

        _timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_settings.HealthCheckIntervalSeconds, 2, 3600) * 1000,
            Enabled = true,
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _mcpMetricsTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_settings.McpMetricsRefreshSeconds, 1, 3600) * 1000,
            Enabled = true,
        };
        _mcpMetricsTimer.Tick += async (_, _) => await RefreshMcpProcessMetricsAsync();

        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

        UpdateModeMenu();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (_serviceMode)
            {
                await RefreshAsync();
                return;
            }

            CreatePortableRuntime();
            if (_settings.AutoStartRuntime)
            {
                await _portableRuntime!.StartAsync();
                if (_settings.OpenBrowserOnPortableStart)
                {
                    OpenFrontend();
                }
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError("MetaMCP startup failed", ex.Message);
            await RefreshAsync();
        }
    }

    private void CreatePortableRuntime()
    {
        _settings = HostSettings.Load(_baseDirectory);
        _portableRuntime = new RuntimeController(_baseDirectory, _settings, new WindowsRuntimePlatform());
    }

    private void BuildMappingMenu()
    {
        _mappingItems.Clear();
        _mappingItem.DropDownItems.Clear();
        foreach (var mapping in _settings.ReverseSsh.Mappings)
        {
            var item = new ToolStripMenuItem(
                $"{mapping.DisplayName}  ({mapping.PublicPath} -> VPS:{mapping.RemotePort})")
            {
                Tag = mapping.Id,
                CheckOnClick = false,
                Checked = mapping.Id.Equals(
                    _settings.ReverseSsh.ActiveMapping,
                    StringComparison.OrdinalIgnoreCase),
            };
            item.Click += async (_, _) => await SelectMappingAsync(mapping.Id);
            _mappingItems[mapping.Id] = item;
            _mappingItem.DropDownItems.Add(item);
        }
    }

    private async Task SelectMappingAsync(string mappingId)
    {
        await RunBusyAsync(async () =>
        {
            RuntimeStatus status;
            if (_serviceMode)
            {
                var response = await PipeClient.SendAsync(
                    PipeCommands.SelectMapping,
                    timeout: TimeSpan.FromSeconds(20),
                    mappingId: mappingId);
                EnsurePipeSuccess(response);
                status = response.Status!;
                _settings = HostSettings.Load(_baseDirectory);
                BuildMappingMenu();
            }
            else
            {
                _portableRuntime ??= new RuntimeController(_baseDirectory, _settings, new WindowsRuntimePlatform());
                status = await _portableRuntime.SwitchReverseSshMappingAsync(mappingId);
            }

            UpdateStatusMenu(status);
            var mapping = _settings.ReverseSsh.GetMapping(mappingId);
            ShowBalloon(
                "Reverse SSH mapping changed",
                $"{mapping.DisplayName}: {mapping.PublicPath} via VPS port {mapping.RemotePort}.",
                ToolTipIcon.Info);
        }, "Could not change reverse SSH mapping");
    }

    private async Task StartRuntimeAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (_serviceMode)
            {
                var response = await PipeClient.SendAsync(PipeCommands.Start);
                EnsurePipeSuccess(response);
            }
            else
            {
                _portableRuntime ??= new RuntimeController(_baseDirectory, _settings, new WindowsRuntimePlatform());
                await _portableRuntime.StartAsync();
            }

            await RefreshAsync();
        }, "Start failed");
    }

    private async Task StopRuntimeAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (_serviceMode)
            {
                var response = await PipeClient.SendAsync(PipeCommands.Stop);
                EnsurePipeSuccess(response);
            }
            else if (_portableRuntime is not null)
            {
                await _portableRuntime.StopAsync();
            }

            await RefreshAsync();
        }, "Stop failed");
    }

    private async Task RestartRuntimeAsync()
    {
        await RunBusyAsync(async () =>
        {
            await RestartRuntimeCoreAsync();
            await RefreshAsync();
        }, "Restart failed");
    }

    private async Task RestartRuntimeCoreAsync()
    {
        if (_serviceMode)
        {
            var response = await PipeClient.SendAsync(
                PipeCommands.Restart,
                TimeSpan.FromSeconds(120));
            EnsurePipeSuccess(response);
        }
        else
        {
            _portableRuntime ??= new RuntimeController(
                _baseDirectory,
                _settings,
                new WindowsRuntimePlatform());
            await _portableRuntime.RestartAsync();
        }
    }


    private async Task ResetMcpConnectionsAsync()
    {
        var confirmation = MessageBox.Show(
            "Close all downstream MCP connections and local MCP processes?\n\n" +
            "Active tool calls will fail. The next tool call will create a fresh connection.",
            "Reset MCP connections",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"http://127.0.0.1:{_settings.BackendPort}/host-control/mcp-connections/reset");
                request.Headers.Add(
                    "X-MetaMCP-Host-Control-Token",
                    _settings.HostControlToken);
                using var response = await _metricsHttp.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await System.Text.Json.JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token);
                var root = document.RootElement;
                var requested = ReadInt(root, "requestedConnections");
                var closed = ReadInt(root, "closedConnections");
                var failed = ReadInt(root, "failedConnections");
                var timedOut = ReadInt(root, "timedOutConnections");
                ShowBalloon(
                    "MCP connections reset",
                    $"Closed {closed}/{requested}. Failed: {failed}. Timed out: {timedOut}.",
                    failed == 0 && timedOut == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
                if (failed > 0 || timedOut > 0)
                {
                    var restart = MessageBox.Show(
                        "Some MCP connections did not close cleanly. Restart the complete runtime to force process-tree cleanup?",
                        "Incomplete MCP reset",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button1);
                    if (restart == DialogResult.Yes)
                    {
                        await RestartRuntimeCoreAsync();
                    }
                }
            }
            catch (Exception resetError)
            {
                HostLog.Error("MCP connection reset endpoint failed.", resetError);
                var restart = MessageBox.Show(
                    "The backend did not complete the MCP reset. Restart the complete MetaMCP runtime instead?",
                    "MCP reset failed",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (restart != DialogResult.Yes)
                {
                    throw;
                }

                await RestartRuntimeCoreAsync();
                ShowBalloon(
                    "MetaMCP restarted",
                    "The backend was unavailable, so the complete runtime was restarted.",
                    ToolTipIcon.Warning);
            }

            await RefreshAsync();
        }, "Reset MCP connections failed");
    }

    private async Task InstallServiceAsync()
    {
        if (_serviceMode || _busy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (_portableRuntime is not null)
            {
                await _portableRuntime.StopAsync();
                await _portableRuntime.DisposeAsync();
                _portableRuntime = null;
            }

            var exitCode = await ServiceInstaller.RunElevatedAsync("--install-service");
            if (exitCode == 1223)
            {
                CreatePortableRuntime();
                await _portableRuntime!.StartAsync();
                return;
            }
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Service installer exited with code {exitCode}.");
            }

            _serviceMode = true;
            UpdateModeMenu();
            await WaitForPipeAsync(TimeSpan.FromSeconds(30));
            await RefreshAsync();
            ShowBalloon(
                "MetaMCP service installed",
                "The service starts with Windows. The tray icon starts after sign-in.",
                ToolTipIcon.Info);
        }, "Service installation failed");
    }

    private async Task UninstallServiceAsync()
    {
        if (!_serviceMode || _busy)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            try
            {
                await PipeClient.SendAsync(PipeCommands.Stop, TimeSpan.FromSeconds(15));
            }
            catch
            {
            }

            var exitCode = await ServiceInstaller.RunElevatedAsync("--uninstall-service");
            if (exitCode == 1223)
            {
                return;
            }
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Service uninstaller exited with code {exitCode}.");
            }

            _serviceMode = false;
            UpdateModeMenu();
            CreatePortableRuntime();
            if (_settings.AutoStartRuntime)
            {
                await _portableRuntime!.StartAsync();
            }
            await RefreshAsync();
        }, "Service removal failed");
    }

    private async Task RefreshAsync()
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            RuntimeStatus status;
            if (_serviceMode)
            {
                if (!ServiceInstaller.IsInstalled())
                {
                    _serviceMode = false;
                    UpdateModeMenu();
                    CreatePortableRuntime();
                    status = await _portableRuntime!.RefreshStatusAsync();
                }
                else
                {
                    var response = await PipeClient.SendAsync(
                        PipeCommands.Status,
                        TimeSpan.FromSeconds(3));
                    EnsurePipeSuccess(response);
                    status = response.Status!;
                }
            }
            else
            {
                _portableRuntime ??= new RuntimeController(_baseDirectory, _settings, new WindowsRuntimePlatform());
                status = await _portableRuntime.RefreshStatusAsync();
            }

            UpdateStatusMenu(status);
            await RefreshConnectionCountsAsync(status.Backend == ComponentState.Online);
        }
        catch (Exception ex)
        {
            var serviceError = _serviceMode
                ? "Windows service is unavailable."
                : ex.Message;
            UpdateStatusMenu(new RuntimeStatus(
                false,
                ComponentState.Offline,
                ComponentState.Offline,
                ComponentState.Offline,
                _settings.ReverseSsh.Enabled ? ComponentState.Offline : ComponentState.Disabled,
                _settings.ReverseSsh.ActiveMapping,
                null,
                null,
                serviceError,
                DateTimeOffset.Now));
            SetConnectionCountsUnavailable();
            SetMcpMetricsUnavailable();
        }
    }

    private async Task RefreshConnectionCountsAsync(bool backendOnline)
    {
        if (!backendOnline)
        {
            SetConnectionCountsUnavailable();
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
            Math.Clamp(_settings.McpTelemetryTimeoutMilliseconds, 1000, 30000)));
        try
        {
            using var response = await _metricsHttp.GetAsync(
                $"http://127.0.0.1:{_settings.BackendPort}/metamcp/health/sessions",
                timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);
            var root = document.RootElement;

            UpdateSessionsTree(
                ReadSessionDetails(root),
                ReadConnectionDetails(root));
        }
        catch (Exception ex)
        {
            SetConnectionCountsUnavailable(ex.Message);
        }
    }

    private async Task RefreshMcpProcessMetricsAsync()
    {
        if (_exiting || _mcpMetricsRefreshInProgress)
        {
            return;
        }

        if (!_backendOnline)
        {
            SetMcpMetricsUnavailable();
            return;
        }

        _mcpMetricsRefreshInProgress = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(
                Math.Clamp(_settings.McpTelemetryTimeoutMilliseconds, 1000, 30000)));
            using var response = await _metricsHttp.GetAsync(
                $"http://127.0.0.1:{_settings.BackendPort}/metamcp/health/sessions",
                timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(
                stream,
                cancellationToken: timeout.Token);

            var processIds = ReadConnectionDetails(document.RootElement)
                .Where(connection => connection.ProcessId.HasValue)
                .Select(connection => connection.ProcessId!.Value);
            _lastMcpMetrics = _mcpMetricsSampler.Sample(processIds);
        }
        catch
        {
            SetMcpMetricsUnavailable();
        }
        finally
        {
            _mcpMetricsRefreshInProgress = false;
            UpdateNotifyTooltip();
        }
    }

    private void SetMcpMetricsUnavailable()
    {
        _lastMcpMetrics = null;
        _mcpMetricsSampler.Reset();
        UpdateNotifyTooltip();
    }

    private static int ReadInt(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long ReadLong(System.Text.Json.JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static IReadOnlyList<McpSessionInfo> ReadSessionDetails(
        System.Text.Json.JsonElement root)
    {
        var result = new List<McpSessionInfo>();
        if (!root.TryGetProperty("streamableHttpSessions", out var container))
        {
            return result;
        }

        if (container.TryGetProperty("sessions", out var sessions) &&
            sessions.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var session in sessions.EnumerateArray())
            {
                var sessionId = ReadString(session, "sessionId");
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    var inFlightOperations = session.TryGetProperty(
                        "inFlightOperations",
                        out _)
                            ? ReadInt(session, "inFlightOperations")
                            : ReadInt(session, "activeRequests");
                    result.Add(new McpSessionInfo(
                        sessionId,
                        inFlightOperations,
                        ReadInt(session, "openEventStreams"),
                        ReadLong(session, "idleMs")));
                }
            }
            return result;
        }

        if (container.TryGetProperty("sessionIds", out var sessionIds) &&
            sessionIds.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var sessionId in sessionIds.EnumerateArray())
            {
                if (sessionId.ValueKind == System.Text.Json.JsonValueKind.String &&
                    sessionId.GetString() is { Length: > 0 } value)
                {
                    result.Add(new McpSessionInfo(value, 0, 0, 0));
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<McpConnectionInfo> ReadConnectionDetails(
        System.Text.Json.JsonElement root)
    {
        var result = new List<McpConnectionInfo>();
        if (!root.TryGetProperty("mcpConnections", out var connections) ||
            connections.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return result;
        }

        foreach (var connection in connections.EnumerateArray())
        {
            var serverName = ReadString(connection, "serverName") ?? "Unknown MCP";
            var serverType = ReadString(connection, "serverType") ?? "UNKNOWN";
            var kind = ReadString(connection, "kind") ?? "UNKNOWN";
            var processId = connection.TryGetProperty("processId", out var processIdValue) &&
                processIdValue.TryGetInt32(out var pid)
                    ? pid
                    : (int?)null;
            var sessionIds = connection.TryGetProperty("sessionIds", out var sessions) &&
                sessions.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? sessions.EnumerateArray()
                        .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(value => value.GetString()!)
                        .ToArray()
                    : [];

            result.Add(new McpConnectionInfo(
                serverName,
                serverType,
                kind,
                processId,
                sessionIds,
                ReadInt(connection, "inFlight")));
        }

        return result;
    }

    private static string? ReadString(
        System.Text.Json.JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private void UpdateSessionsTree(
        IReadOnlyList<McpSessionInfo> sessions,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        _lastSessionCount = sessions.Count;
        _lastConnectionCount = connections.Count;
        _lastMcpTelemetryAt = DateTimeOffset.Now;
        _sessionsItem.ToolTipText = string.Empty;
        SetActivityItem(
            _sessionsItem,
            $"MCP: {sessions.Count} sessions | {connections.Count} connections",
            sessions.Count > 0 || connections.Count > 0 ? _greenDot : _grayDot);
        _sessionsItem.DropDownItems.Clear();
        _sessionsItem.DropDownItems.Add(CreateMcpConnectionsItem(connections));

        var activeSessionsItem = new ToolStripMenuItem(
            $"Client sessions: {sessions.Count}");
        var sessionEntries = sessions
            .Select(session => new
            {
                Session = session,
                Connections = connections
                    .Where(connection => connection.SessionIds.Contains(
                        session.SessionId,
                        StringComparer.Ordinal))
                    .ToArray(),
            })
            .OrderBy(entry => entry.Connections.FirstOrDefault()?.ServerName ?? string.Empty)
            .ThenBy(entry => entry.Connections.FirstOrDefault()?.ProcessId ?? int.MaxValue)
            .ThenBy(entry => entry.Session.SessionId)
            .ToArray();
        foreach (var entry in sessionEntries)
        {
            var session = entry.Session;
            var linkedConnections = entry.Connections;
            var sessionItem = new ToolStripMenuItem(
                BuildSessionMenuText(session, linkedConnections))
            {
                ToolTipText =
                    "MCP operations are active POST/tool requests. Event streams are " +
                    "long-lived GET channels and do not keep an idle session alive.",
            };
            sessionItem.DropDownItems.Add(CreateDisabledMenuItem(
                $"Session ID: {session.SessionId}"));
            sessionItem.DropDownItems.Add(CreateDisabledMenuItem(
                $"MCP operations: {session.InFlightOperations}"));
            sessionItem.DropDownItems.Add(CreateDisabledMenuItem(
                $"Event streams: {session.OpenEventStreams}"));
            sessionItem.DropDownItems.Add(CreateDisabledMenuItem(
                $"Idle: {FormatIdleDuration(session.IdleMilliseconds)}"));
            sessionItem.DropDownItems.Add(new ToolStripSeparator());
            AddConnectionGroups(sessionItem, linkedConnections);
            activeSessionsItem.DropDownItems.Add(sessionItem);
        }

        if (sessions.Count == 0)
        {
            activeSessionsItem.Enabled = false;
        }
        _sessionsItem.DropDownItems.Add(activeSessionsItem);
    }

    private static string BuildSessionMenuText(
        McpSessionInfo session,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        if (connections.Count == 0)
        {
            return $"Session {ShortSessionId(session.SessionId)}";
        }

        if (connections.Count == 1)
        {
            var connection = connections[0];
            var process = connection.ProcessId is int pid
                ? $"PID {pid}"
                : connection.ServerType;
            return $"{connection.ServerName} [{FormatConnectionKind(connection.Kind)}] | " +
                process;
        }

        var serverNames = connections
            .Select(connection => connection.ServerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .Take(2)
            .ToArray();
        var remaining = connections
            .Select(connection => connection.ServerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() - serverNames.Length;
        var names = string.Join(", ", serverNames) +
            (remaining > 0 ? $" +{remaining}" : string.Empty);
        return $"{names} | {connections.Count} connections";
    }

    private static ToolStripMenuItem CreateMcpConnectionsItem(
        IReadOnlyList<McpConnectionInfo> connections)
    {
        var root = new ToolStripMenuItem(
            $"MetaMCP → MCP connections: {connections.Count}");
        foreach (var kind in new[] { "PERSISTENT", "SESSION", "IDLE" })
        {
            var matching = connections
                .Where(connection => connection.Kind == kind)
                .ToArray();
            var item = new ToolStripMenuItem(
                $"{FormatConnectionKind(kind)}: {matching.Length}");
            if (matching.Length == 0)
            {
                item.Enabled = false;
            }
            else
            {
                AddServerGroups(item, matching);
            }
            root.DropDownItems.Add(item);
        }
        return root;
    }

    private static void AddConnectionGroups(
        ToolStripMenuItem parent,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        if (connections.Count == 0)
        {
            parent.DropDownItems.Add(CreateDisabledMenuItem("No MCP connections"));
            return;
        }

        AddServerGroups(parent, connections);
    }

    private static void AddServerGroups(
        ToolStripMenuItem parent,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        foreach (var serverGroup in connections
            .GroupBy(connection => new
            {
                connection.ServerName,
                connection.ServerType,
                connection.Kind,
            })
            .OrderBy(group => group.Key.ServerName))
        {
            var shared = serverGroup.Key.Kind == "PERSISTENT"
                ? " | shared"
                : string.Empty;
            var serverItem = new ToolStripMenuItem(
                $"{serverGroup.Key.ServerName} [{FormatConnectionKind(serverGroup.Key.Kind)}]{shared}");

            foreach (var connection in serverGroup.OrderBy(item => item.ProcessId ?? int.MaxValue))
            {
                var processText = connection.ProcessId is int pid
                    ? $"PID: {pid} | Transport: {connection.ServerType}"
                    : $"Transport: {connection.ServerType}";
                var active = connection.InFlight > 0
                    ? $" | MCP requests: {connection.InFlight}"
                    : string.Empty;
                serverItem.DropDownItems.Add(CreateDisabledMenuItem(
                    $"{processText}{active}"));
            }

            parent.DropDownItems.Add(serverItem);
        }
    }

    private static string FormatConnectionKind(string kind) =>
        kind switch
        {
            "PERSISTENT" => "persistent",
            "SESSION" => "session",
            "IDLE" => "idle",
            _ => kind.ToLowerInvariant(),
        };

    private static string ShortSessionId(string sessionId) =>
        sessionId.Length <= 8 ? sessionId : sessionId[..8];

    private static string FormatIdleDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (duration.TotalSeconds < 1)
        {
            return "<1s";
        }
        if (duration.TotalMinutes < 1)
        {
            return $"{(int)duration.TotalSeconds}s";
        }
        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }
        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    private static ToolStripMenuItem CreateDisabledMenuItem(string text) =>
        new(text)
        {
            Enabled = false,
        };

    private static void SetActivityItem(
        ToolStripMenuItem item,
        string text,
        Image image)
    {
        item.Text = text;
        item.Image = image;
    }

    private void SetConnectionCountsUnavailable(string? reason = null)
    {
        if (_backendOnline &&
            _lastSessionCount is int sessions &&
            _lastConnectionCount is int connections)
        {
            var age = _lastMcpTelemetryAt.HasValue
                ? FormatIdleDuration((long)(DateTimeOffset.Now - _lastMcpTelemetryAt.Value).TotalMilliseconds)
                : "unknown";
            SetActivityItem(
                _sessionsItem,
                $"MCP telemetry delayed | last: {sessions} sessions | {connections} connections",
                _yellowDot);
            _sessionsItem.ToolTipText =
                $"Last successful telemetry: {age} ago. {reason}".Trim();
            return;
        }

        SetActivityItem(
            _sessionsItem,
            _backendOnline ? "MCP telemetry unavailable" : "MCP: backend unavailable",
            _backendOnline ? _yellowDot : _redDot);
        _sessionsItem.ToolTipText = reason ?? string.Empty;
        _sessionsItem.DropDownItems.Clear();
        _sessionsItem.DropDownItems.Add(CreateDisabledMenuItem(
            _backendOnline ? "Telemetry request failed" : "Backend is offline"));
    }

    private void UpdateStatusMenu(RuntimeStatus status)
    {
        SetOverallItem(status.Overall);
        SetComponentItem(_backendItem, "Backend", status.Backend);
        SetComponentItem(_frontendItem, "Frontend", status.Frontend);
        SetComponentItem(_databaseItem, "PostgreSQL", status.Database);
        var mapping = ResolveMapping(status.ReverseSshMappingId);
        SetComponentItem(
            _sshItem,
            mapping is null ? "Reverse SSH" : $"Reverse SSH [{mapping.DisplayName}]",
            status.ReverseSsh);
        UpdateMappingSelection(status.ReverseSshMappingId);
        _mappingItem.Enabled = !_busy && _settings.ReverseSsh.Enabled;
        _startItem.Enabled = !_busy && !status.DesiredRunning;
        _stopItem.Enabled = !_busy && status.DesiredRunning;
        _restartItem.Enabled = !_busy && status.DesiredRunning;
        _resetMcpConnectionsItem.Enabled = !_busy &&
            status.Backend == ComponentState.Online;

        _backendOnline = status.Backend == ComponentState.Online;
        _lastOverallState = status.Overall;
        UpdateNotifyTooltip();
    }

    private void UpdateNotifyTooltip()
    {
        var status = GetOverallText(_lastOverallState);
        var tooltip = _lastMcpMetrics is { } metrics
            ? $"MetaMCP {status} | MCP {metrics.ProcessCount} | CPU {metrics.CpuPercent:0.0}% | RAM {FormatMemory(metrics.WorkingSetBytes)}"
            : $"MetaMCP {status} | MCP metrics unavailable";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private static string FormatMemory(long bytes)
    {
        const double megabyte = 1024d * 1024d;
        const double gigabyte = 1024d * 1024d * 1024d;
        return bytes >= gigabyte
            ? $"{bytes / gigabyte:0.0} GB"
            : $"{bytes / megabyte:0} MB";
    }

    private ReverseSshMappingSettings? ResolveMapping(string? mappingId)
    {
        var id = string.IsNullOrWhiteSpace(mappingId)
            ? _settings.ReverseSsh.ActiveMapping
            : mappingId;
        return _settings.ReverseSsh.Mappings.FirstOrDefault(mapping =>
            mapping.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateMappingSelection(string? mappingId)
    {
        var activeId = string.IsNullOrWhiteSpace(mappingId)
            ? _settings.ReverseSsh.ActiveMapping
            : mappingId;
        foreach (var pair in _mappingItems)
        {
            pair.Value.Checked = pair.Key.Equals(
                activeId,
                StringComparison.OrdinalIgnoreCase);
        }

        var mapping = ResolveMapping(activeId);
        _mappingItem.Text = mapping is null
            ? "Reverse SSH mapping"
            : $"Tunnel: {mapping.DisplayName} ({mapping.PublicPath})";
    }

    private void SetOverallItem(OverallState state)
    {
        _overallItem.Text = $"Status: {GetOverallText(state)}";
        _overallItem.Image = state switch
        {
            OverallState.Online => _greenDot,
            OverallState.Starting or OverallState.Degraded => _yellowDot,
            _ => _redDot,
        };
    }

    private void SetComponentItem(
        ToolStripMenuItem item,
        string name,
        ComponentState state)
    {
        item.Text = $"{name}: {GetComponentText(state)}";
        item.Image = state switch
        {
            ComponentState.Online => _greenDot,
            ComponentState.Starting => _yellowDot,
            ComponentState.Disabled => _grayDot,
            _ => _redDot,
        };
    }

    private void UpdateModeMenu()
    {
        _modeItem.Text = _serviceMode
            ? "Mode: Windows service"
            : "Mode: portable";
        _installServiceItem.Visible = !_serviceMode;
        _uninstallServiceItem.Visible = _serviceMode;
    }

    private async Task RunBusyAsync(Func<Task> action, string errorTitle)
    {
        if (_busy || _exiting)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            HostLog.Error(errorTitle, ex);
            ShowError(errorTitle, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _startItem.Enabled = !busy;
        _stopItem.Enabled = !busy;
        _restartItem.Enabled = !busy;
        _resetMcpConnectionsItem.Enabled = !busy && _backendOnline;
        _mappingItem.Enabled = !busy && _settings.ReverseSsh.Enabled;
        _installServiceItem.Enabled = !busy;
        _uninstallServiceItem.Enabled = !busy;
    }

    private async Task WaitForPipeAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await PipeClient.SendAsync(
                    PipeCommands.Status,
                    TimeSpan.FromSeconds(2));
                if (response.Success)
                {
                    return;
                }
            }
            catch
            {
            }
            await Task.Delay(500);
        }

        throw new TimeoutException("The Windows service did not open its control pipe.");
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();
        _mcpMetricsTimer.Stop();
        try
        {
            if (!_serviceMode && _portableRuntime is not null)
            {
                await _portableRuntime.StopAsync();
                await _portableRuntime.DisposeAsync();
                _portableRuntime = null;
            }
        }
        catch (Exception ex)
        {
            HostLog.Error("Portable runtime shutdown failed.", ex);
        }
        finally
        {
            SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
            _notifyIcon.Visible = false;
            _timer.Dispose();
            _mcpMetricsTimer.Dispose();
            _metricsHttp.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _applicationIcon.Dispose();
            _appMenuIcon.Dispose();
            _jsonIcon.Dispose();
            _greenDot.Dispose();
            _yellowDot.Dispose();
            _redDot.Dispose();
            _grayDot.Dispose();
            ExitThread();
        }
    }

    private void OpenFrontend()
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                $"http://localhost:{_settings.FrontendPort}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowError("Could not open MetaMCP", ex.Message);
        }
    }

    private void OpenConfiguration()
    {
        var path = HostSettings.GetConfigPath(_baseDirectory);
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private static void EnsurePipeSuccess(PipeResponse response)
    {
        if (!response.Success || response.Status is null)
        {
            throw new InvalidOperationException(response.Error ?? "The service command failed.");
        }
    }

    private void ShowError(string title, string message)
    {
        ShowBalloon(title, message, ToolTipIcon.Error);
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message.Length <= 240 ? message : message[..240];
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(4000);
    }

    private static ToolStripMenuItem CreateStatusItem(string text) =>
        new(text)
        {
            Enabled = true,
            ImageScaling = ToolStripItemImageScaling.None,
            AutoToolTip = false,
        };

    private static ToolStripMenuItem CreateInformationItem(string text) =>
        new(text)
        {
            Enabled = true,
            AutoToolTip = false,
        };

    private static Image CreateDot(Color color)
    {
        var bitmap = new Bitmap(14, 14);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 12, 12);
        return bitmap;
    }

    private static Image CreateJsonIcon(Color foreground, Color background)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.Clear(Color.Transparent);

        // Document body
        using var bodyBrush = new SolidBrush(background);
        g.FillRectangle(bodyBrush, 3, 1, 10, 13);

        // Fold corner
        using var foldBrush = new SolidBrush(Color.FromArgb(
            Math.Max(0, background.R - 40),
            Math.Max(0, background.G - 40),
            Math.Max(0, background.B - 40)));
        g.FillPolygon(foldBrush, new Point[] { new(11, 1), new(13, 1), new(13, 4), new(11, 2) });

        // Border
        using var pen = new Pen(foreground, 1f);
        g.DrawRectangle(pen, 3, 1, 10, 13);

        // "{ }" text
        using var font = new Font("Segoe UI", 5.5f, FontStyle.Bold);
        using var textBrush = new SolidBrush(foreground);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString("{ }", font, textBrush, new RectangleF(2, 3, 12, 11), sf);

        return bitmap;
    }

    private static string GetComponentText(ComponentState state) => state switch
    {
        ComponentState.Online => "online",
        ComponentState.Starting => "starting",
        ComponentState.Disabled => "disabled",
        ComponentState.Error => "error",
        _ => "offline",
    };

    private static string GetOverallText(OverallState state) => state switch
    {
        OverallState.Online => "online",
        OverallState.Starting => "starting",
        OverallState.Degraded => "degraded",
        OverallState.Error => "error",
        _ => "offline",
    };

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int v && v != 0;
        }
        catch
        {
            return true;
        }
    }

    private static Image CreateJsonForCurrentTheme()
    {
        var resourceName = IsLightTheme() ? "json-light.png" : "json-dark.png";
        return LoadEmbeddedIcon(resourceName) ?? CreateJsonFallback();
    }

    private static Image CreateJsonFallback()
    {
        return IsLightTheme()
            ? CreateJsonIcon(Color.FromArgb(80, 80, 80), Color.FromArgb(240, 240, 240))
            : CreateJsonIcon(Color.FromArgb(200, 200, 200), Color.FromArgb(50, 50, 50));
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        try
        {
            var newIcon = CreateJsonForCurrentTheme();
            var old = Interlocked.Exchange(ref _jsonIcon, newIcon);
            _openConfigItem.Image = newIcon;
            old?.Dispose();
        }
        catch { }
    }

    private static Image? LoadEmbeddedIcon(string fileName)
    {
        try
        {
            var assembly = typeof(TrayApplicationContext).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return null;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream is not null ? Image.FromStream(stream) : null;
        }
        catch
        {
            return null;
        }
    }
}
