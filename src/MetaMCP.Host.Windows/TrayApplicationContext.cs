using System.Diagnostics;
using Microsoft.Win32;

namespace MetaMCP.Host;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int TrayMenuWidth = 300;
    private const int McpTelemetryRetryCount = 2;

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

    private sealed record McpTelemetrySnapshot(
        IReadOnlyList<McpSessionInfo> Sessions,
        IReadOnlyList<McpConnectionInfo> Connections);

    private readonly string _baseDirectory;
    private HostSettings _settings;
    private RuntimeSlotManager? _runtime;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _summaryItem;
    private readonly ToolStripControlHost _statusTableHost;
    private readonly StatusTableControl _statusTable;
    private readonly ToolStripMenuItem _sessionsItem;
    private readonly ToolStripMenuItem _mappingItem;
    private readonly Dictionary<string, ToolStripMenuItem> _mappingItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolStripMenuItem _startRestartItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _resetMcpConnectionsItem;
    private readonly ToolStripMenuItem _resetReverseSshItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _openWebItem;
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
    private bool _runtimeDesiredRunning;
    private bool _mcpTelemetryRefreshInProgress;
    private OverallState _lastOverallState = OverallState.Offline;
    private McpProcessMetrics? _lastMcpMetrics;
    private McpTelemetrySnapshot? _latestMcpTelemetry;
    private bool _pendingMcpMenuRefresh;
    private int _consecutiveMcpTelemetryFailures;
    private int? _lastSessionCount;
    private int? _lastConnectionCount;
    private Icon? _generatedTrayIcon;
    private int _displayedTrayConnectionCount = int.MinValue;
    private DateTimeOffset? _lastMcpTelemetryAt;

    public TrayApplicationContext(string? baseDirectory = null)
    {
        _baseDirectory = (baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _settings = HostSettings.Load(_baseDirectory);
        HostLog.Initialize(_baseDirectory, fileEnabled: true);

        _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _appMenuIcon = LoadEmbeddedIcon("metamcp_32.png") ?? _applicationIcon.ToBitmap();
        _jsonIcon = CreateJsonForCurrentTheme();
        _menu = new ContextMenuStrip
        {
            AutoSize = true,
            MinimumSize = new Size(TrayMenuWidth, 0),
            MaximumSize = new Size(TrayMenuWidth, 0),
        };
        _summaryItem = CreateInformationItem(string.Empty);
        _statusTable = new StatusTableControl();
        _statusTableHost = new ToolStripControlHost(_statusTable)
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _sessionsItem = CreateStatusItem("Sessions: checking...");
        _mappingItem = new ToolStripMenuItem("Reverse SSH mapping");
        BuildMappingMenu();
        _menu.Items.AddRange([
            _summaryItem,
            _sessionsItem,
            new ToolStripSeparator(),
            _statusTableHost,
            new ToolStripSeparator(),
            _mappingItem,
            new ToolStripSeparator(),
        ]);

        _openWebItem = new ToolStripMenuItem(
            "MetaMCP Webs",
            _appMenuIcon,
            (_, _) => OpenFrontend())
        {
            ToolTipText = "Open the MetaMCP web interface.",
        };
        _openConfigItem = new ToolStripMenuItem("Open configuration", _jsonIcon, (_, _) => OpenConfiguration());
        _menu.Items.Add(_openWebItem);
        _menu.Items.Add(_openConfigItem);
        _menu.Items.Add(new ToolStripSeparator());
        _startRestartItem = new ToolStripMenuItem(
            "Start",
            null,
            async (_, _) => await StartOrRestartRuntimeAsync());
        _stopItem = new ToolStripMenuItem("Stop", null, async (_, _) => await StopRuntimeAsync());
        _resetMcpConnectionsItem = new ToolStripMenuItem(
            "Reset MCP connections",
            null,
            async (_, _) => await ResetMcpConnectionsAsync())
        {
            ToolTipText = "Close all downstream MCP servers without stopping MetaMCP.",
        };
        _resetReverseSshItem = new ToolStripMenuItem(
            "Technical SSH reset",
            null,
            async (_, _) => await ResetReverseSshAsync())
        {
            ToolTipText =
                "Restart the tunnel and clear stale remote sshd listeners on the active VPS port.",
        };
        _menu.Items.AddRange([_startRestartItem, _stopItem]);
        _menu.Items.Add(_resetMcpConnectionsItem);
        _menu.Items.Add(_resetReverseSshItem);
        _menu.Items.Add(new ToolStripSeparator());
        _versionItem = new ToolStripMenuItem(
            $"Version: {GetDisplayVersion()} · {GetLaunchFolderDisplayName()} · Runtime ?")
        {
            Enabled = false,
        };
        _menu.Items.Add(_versionItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "MetaMCP starting...",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _menu.Opening += (_, _) =>
        {
            ApplyPendingMcpMenuRefresh();
            StretchTopLevelItems();
        };
        _menu.Opened += (_, _) => StretchTopLevelItems();
        _menu.Closed += (_, _) => ApplyPendingMcpMenuRefresh();
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
        _mcpMetricsTimer.Tick += async (_, _) => await RefreshMcpTelemetryAsync();

        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;

        UpdateSummaryMenu();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            CreateRuntimeManager();
            if (_settings.AutoStartRuntime)
            {
                await _runtime!.StartAsync();
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

    private void CreateRuntimeManager()
    {
        _settings = HostSettings.Load(_baseDirectory);
        _runtime = new RuntimeSlotManager(_baseDirectory, _settings);
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
            _runtime ??= new RuntimeSlotManager(_baseDirectory, _settings);
            var status = await _runtime.SwitchReverseSshMappingAsync(mappingId);
            _settings = HostSettings.Load(_baseDirectory);
            BuildMappingMenu();

            UpdateStatusMenu(status);
            var mapping = _settings.ReverseSsh.GetMapping(mappingId);
            ShowBalloon(
                "Reverse SSH mapping changed",
                $"{mapping.DisplayName}: {mapping.PublicPath} via VPS port {mapping.RemotePort}.",
                ToolTipIcon.Info);
        }, "Could not change reverse SSH mapping");
    }
    private async Task StartOrRestartRuntimeAsync()
    {
        var shouldRestart = _runtimeDesiredRunning;
        await RunBusyAsync(async () =>
        {
            if (shouldRestart)
            {
                await RestartRuntimeCoreAsync();
            }
            else
            {
                await StartRuntimeCoreAsync();
            }

            await RefreshAsync();
        }, shouldRestart ? "Restart failed" : "Start failed");
    }

    private async Task StartRuntimeCoreAsync()
    {
        _runtime ??= new RuntimeSlotManager(_baseDirectory, _settings);
        await _runtime.StartAsync();
    }
    private async Task StopRuntimeAsync()
    {
        await RunBusyAsync(async () =>
        {
            if (_runtime is not null)
            {
                await _runtime.StopAsync();
            }

            await RefreshAsync();
        }, "Stop failed");
    }
    private async Task RestartRuntimeCoreAsync()
    {
        _runtime ??= new RuntimeSlotManager(_baseDirectory, _settings);
        await _runtime.RestartAsync();
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

    private async Task ResetReverseSshAsync()
    {
        var mapping = ResolveMapping(_settings.ReverseSsh.ActiveMapping);
        if (mapping is null)
        {
            throw new InvalidOperationException(
                "The active Reverse SSH mapping is missing from host.json.");
        }

        var confirmation = MessageBox.Show(
            $"Restart Reverse SSH [{mapping.DisplayName}] and clear stale remote SSH listeners?\n\n" +
            $"Only sshd sessions listening on VPS port {mapping.RemotePort} will be terminated.\n" +
            "SSH port 22, other mapping ports and VPS services will not be changed.",
            "Technical SSH reset",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            _runtime ??= new RuntimeSlotManager(_baseDirectory, _settings);
            var result = await _runtime.ResetReverseSshAsync(timeout.Token);
            var status = await _runtime.RefreshStatusAsync(timeout.Token);

            UpdateStatusMenu(status);
            ShowBalloon(
                "Reverse SSH technical reset",
                result.Summary + " Tunnel is online.",
                status.ReverseSsh == ComponentState.Online
                    ? ToolTipIcon.Info
                    : ToolTipIcon.Warning);
            await RefreshAsync();
        }, "Reverse SSH technical reset failed");
    }
    private async Task RefreshAsync()
    {
        if (_exiting)
        {
            return;
        }

        try
        {
            _runtime ??= new RuntimeSlotManager(_baseDirectory, _settings);
            var status = await _runtime.RefreshStatusAsync();

            UpdateStatusMenu(status);
            if (status.Backend == ComponentState.Online &&
                _latestMcpTelemetry is null)
            {
                await RefreshMcpTelemetryAsync();
            }
            else if (status.Backend != ComponentState.Online)
            {
                SetConnectionCountsUnavailable();
                SetMcpMetricsUnavailable();
            }
        }
        catch (Exception ex)
        {
            UpdateStatusMenu(new RuntimeStatus(
                false,
                ComponentState.Offline,
                ComponentState.Offline,
                ComponentState.Offline,
                _settings.ReverseSsh.Enabled ? ComponentState.Offline : ComponentState.Disabled,
                _settings.ReverseSsh.ActiveMapping,
                null,
                null,
                ex.Message,
                DateTimeOffset.Now));
            SetConnectionCountsUnavailable();
            SetMcpMetricsUnavailable();
        }
    }
    private async Task RefreshMcpTelemetryAsync()
    {
        if (_exiting || _mcpTelemetryRefreshInProgress)
        {
            return;
        }

        if (!_backendOnline)
        {
            SetConnectionCountsUnavailable();
            SetMcpMetricsUnavailable();
            return;
        }

        _mcpTelemetryRefreshInProgress = true;
        try
        {
            var snapshot = await FetchMcpTelemetrySnapshotAsync();
            _latestMcpTelemetry = snapshot;
            _consecutiveMcpTelemetryFailures = 0;
            ApplyMcpTelemetrySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            _consecutiveMcpTelemetryFailures++;
            HostLog.Warn($"MCP telemetry refresh failed: {ex.Message}");
            HandleMcpTelemetryFailure(ex.Message);
        }
        finally
        {
            _mcpTelemetryRefreshInProgress = false;
            UpdateNotifyTooltip();
        }
    }

    private async Task<McpTelemetrySnapshot> FetchMcpTelemetrySnapshotAsync()
    {
        Exception? lastError = null;
        var timeoutMilliseconds = Math.Clamp(
            _settings.McpTelemetryTimeoutMilliseconds,
            1000,
            30000);

        for (var attempt = 1; attempt <= McpTelemetryRetryCount; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(timeoutMilliseconds));
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"http://127.0.0.1:{_settings.BackendPort}/metamcp/health/sessions");
                using var response = await _metricsHttp.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await System.Text.Json.JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token);

                return new McpTelemetrySnapshot(
                    ReadSessionDetails(document.RootElement),
                    ReadConnectionDetails(document.RootElement));
            }
            catch (Exception ex) when (attempt < McpTelemetryRetryCount && !_exiting)
            {
                lastError = ex;
                HostLog.Warn(
                    $"MCP telemetry attempt {attempt}/{McpTelemetryRetryCount} failed: {ex.Message}");
                await Task.Delay(150 * attempt);
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        throw lastError ?? new InvalidOperationException("MCP telemetry request failed.");
    }

    private void ApplyMcpTelemetrySnapshot(McpTelemetrySnapshot snapshot)
    {
        _lastSessionCount = snapshot.Sessions.Count;
        _lastConnectionCount = snapshot.Connections.Count;
        _lastMcpTelemetryAt = DateTimeOffset.Now;
        UpdateTrayIconBadge(snapshot.Connections.Count);

        var processIds = snapshot.Connections
            .Where(connection => connection.ProcessId.HasValue)
            .Select(connection => connection.ProcessId!.Value);
        _lastMcpMetrics = _mcpMetricsSampler.Sample(processIds);

        if (IsMcpMenuOpen())
        {
            _pendingMcpMenuRefresh = true;
            return;
        }

        UpdateSessionsTree(snapshot.Sessions, snapshot.Connections);
        _pendingMcpMenuRefresh = false;
    }

    private void ApplyPendingMcpMenuRefresh()
    {
        if (!_pendingMcpMenuRefresh ||
            _latestMcpTelemetry is not { } snapshot ||
            IsMcpMenuOpen())
        {
            return;
        }

        UpdateSessionsTree(snapshot.Sessions, snapshot.Connections);
        _pendingMcpMenuRefresh = false;
    }

    private bool IsMcpMenuOpen() =>
        _menu.Visible || _sessionsItem.DropDown.Visible;

    private void HandleMcpTelemetryFailure(string reason)
    {
        if (_lastMcpTelemetryAt.HasValue && _consecutiveMcpTelemetryFailures < 3)
        {
            var age = FormatIdleDuration((long)(
                DateTimeOffset.Now - _lastMcpTelemetryAt.Value).TotalMilliseconds);
            _sessionsItem.ToolTipText =
                $"Telemetry refresh failed; keeping the last valid snapshot ({age} old). {reason}";
            return;
        }

        SetConnectionCountsUnavailable(reason);
        SetMcpMetricsUnavailable();
    }

    private void SetMcpMetricsUnavailable()
    {
        _lastMcpMetrics = null;
        _mcpMetricsSampler.Reset();
        UpdateNotifyTooltip();
    }

    private static int ReadInt(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            System.Text.Json.JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => 0,
        };
    }

    private static long ReadLong(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            System.Text.Json.JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => 0L,
        };
    }

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
                processIdValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
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
        var activeSessionCount = sessions.Count(
            session => session.InFlightOperations > 0);

        _sessionsItem.ToolTipText =
            "Connections = downstream MetaMCP -> MCP server connections; " +
            "Sessions = client -> MetaMCP sessions; " +
            "Active = sessions with in-flight MCP operations.";
        SetActivityItem(
            _sessionsItem,
            $"MCP connections: {connections.Count} | Client sessions: {sessions.Count} | Active: {activeSessionCount}",
            sessions.Count > 0 || connections.Count > 0 ? _greenDot : _grayDot);
        _sessionsItem.DropDownItems.Clear();

        AddFlattenedConnectionGroups(_sessionsItem, sessions, connections);
        AddOrphanSessions(_sessionsItem, sessions, connections);

        if (_sessionsItem.DropDownItems.Count == 0)
        {
            _sessionsItem.DropDownItems.Add(
                CreateDisabledMenuItem("No MCP sessions or connections"));
        }
    }

    private static void AddFlattenedConnectionGroups(
        ToolStripMenuItem parent,
        IReadOnlyList<McpSessionInfo> sessions,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        var sessionById = sessions.ToDictionary(
            session => session.SessionId,
            StringComparer.Ordinal);

        foreach (var serverGroup in connections
            .GroupBy(connection => new
            {
                connection.ServerName,
                connection.ServerType,
                connection.Kind,
            })
            .OrderBy(group => group.Key.ServerName)
            .ThenBy(group => group.Key.Kind))
        {
            var groupedConnections = serverGroup
                .OrderBy(connection => connection.ProcessId ?? int.MaxValue)
                .ToArray();
            var countSuffix = groupedConnections.Length > 1
                ? $" ×{groupedConnections.Length}"
                : string.Empty;
            var serverItem = new ToolStripMenuItem(
                $"{serverGroup.Key.ServerName} " +
                $"[{FormatConnectionKind(serverGroup.Key.Kind)}]{countSuffix}")
            {
                ToolTipText = $"Transport: {serverGroup.Key.ServerType}",
            };

            foreach (var connection in groupedConnections)
            {
                var linkedSessions = connection.SessionIds
                    .Where(sessionById.ContainsKey)
                    .Select(sessionId => sessionById[sessionId])
                    .ToArray();
                serverItem.DropDownItems.Add(CreateDisabledMenuItem(
                    BuildConnectionEntryText(connection, linkedSessions)));
            }

            parent.DropDownItems.Add(serverItem);
        }
    }

    private static string BuildConnectionEntryText(
        McpConnectionInfo connection,
        IReadOnlyList<McpSessionInfo> sessions)
    {
        var parts = new List<string>();
        if (connection.ProcessId is int pid)
        {
            parts.Add($"PID {pid}");
        }

        if (!string.IsNullOrWhiteSpace(connection.ServerType))
        {
            parts.Add($"Transport {connection.ServerType}");
        }

        if (connection.SessionIds.Length == 1)
        {
            parts.Add($"Session {ShortSessionId(connection.SessionIds[0])}");
        }
        else if (connection.SessionIds.Length > 1)
        {
            parts.Add($"Sessions {connection.SessionIds.Length}");
        }

        if (connection.InFlight > 0)
        {
            parts.Add($"Active {connection.InFlight}");
        }

        if (sessions.Count == 1)
        {
            parts.Add($"Idle {FormatIdleDuration(sessions[0].IdleMilliseconds)}");
        }

        return string.Join(" | ", parts);
    }

    private static void AddOrphanSessions(
        ToolStripMenuItem parent,
        IReadOnlyList<McpSessionInfo> sessions,
        IReadOnlyList<McpConnectionInfo> connections)
    {
        var linkedSessionIds = connections
            .SelectMany(connection => connection.SessionIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var session in sessions
            .Where(session => !linkedSessionIds.Contains(session.SessionId))
            .OrderBy(session => session.SessionId))
        {
            var activity = session.InFlightOperations > 0
                ? $"Active {session.InFlightOperations}"
                : $"Idle {FormatIdleDuration(session.IdleMilliseconds)}";
            parent.DropDownItems.Add(CreateDisabledMenuItem(
                $"Session {ShortSessionId(session.SessionId)} [no MCP] · {activity}"));
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
                $"MCP delayed · last {sessions} sessions / {connections} connections",
                _yellowDot);
            _sessionsItem.ToolTipText =
                $"Last successful telemetry: {age} ago. {reason}".Trim();
            return;
        }

        UpdateTrayIconBadge(null);
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
        _runtimeDesiredRunning = status.DesiredRunning;
        SetOverallStatus(status.Overall);
        SetComponentStatus(1, "Backend", status.Backend);
        SetComponentStatus(2, "Frontend", status.Frontend);
        SetComponentStatus(3, "PostgreSQL", status.Database);
        SetComponentStatus(4, "Reverse SSH", status.ReverseSsh);
        UpdateMappingSelection(status.ReverseSshMappingId);
        _mappingItem.Enabled = !_busy && _settings.ReverseSsh.Enabled;
        _startRestartItem.Text = status.DesiredRunning ? "Restart" : "Start";
        _startRestartItem.ToolTipText = status.DesiredRunning
            ? "Restart MetaMCP runtime."
            : "Start MetaMCP runtime.";
        _startRestartItem.Enabled = !_busy;
        _stopItem.Enabled = !_busy && status.DesiredRunning;
        _resetMcpConnectionsItem.Enabled = !_busy &&
            status.Backend == ComponentState.Online;
        _resetReverseSshItem.Enabled = !_busy &&
            status.DesiredRunning &&
            _settings.ReverseSsh.Enabled;

        _backendOnline = status.Backend == ComponentState.Online;
        if (!_backendOnline)
        {
            UpdateTrayIconBadge(null);
        }
        _lastOverallState = status.Overall;
        UpdateNotifyTooltip();
    }

    private void UpdateNotifyTooltip()
    {
        UpdateSummaryMenu();
        var status = GetOverallText(_lastOverallState);
        var connections = _lastConnectionCount ?? 0;
        var tooltip = _lastMcpMetrics is { } metrics
            ? $"MetaMCP {status} | MCP {connections} | CPU {metrics.CpuPercent:0.0}% | RAM {FormatMemory(metrics.WorkingSetBytes)}"
            : $"MetaMCP {status} | MCP {connections} | metrics unavailable";
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void UpdateTrayIconBadge(int? connectionCount)
    {
        var normalizedCount = Math.Clamp(connectionCount ?? 0, 0, 100);
        if (_displayedTrayConnectionCount == normalizedCount)
        {
            return;
        }

        var nextIcon = normalizedCount > 0
            ? TrayIconBadgeRenderer.CreateIcon(_applicationIcon, normalizedCount)
            : null;
        var previousIcon = _generatedTrayIcon;
        _notifyIcon.Icon = nextIcon ?? _applicationIcon;
        _generatedTrayIcon = nextIcon;
        _displayedTrayConnectionCount = normalizedCount;
        previousIcon?.Dispose();
    }

    private static string GetDisplayVersion()
    {
        var version = Application.ProductVersion;
        var separator = version.IndexOf('+');
        return separator >= 0 ? version[..separator] : version;
    }

    private static string GetLaunchFolderDisplayName()
    {
        var executableDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return "unknown";
        }

        var leaf = new DirectoryInfo(executableDirectory);
        if (leaf.Name.Equals("win-x64", StringComparison.OrdinalIgnoreCase) && leaf.Parent is not null)
        {
            return $"{leaf.Parent.Name}\\{leaf.Name}";
        }

        return leaf.Name;
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

    private void SetOverallStatus(OverallState state)
    {
        _statusTable.SetRow(
            0,
            $"Status: {GetOverallText(state)}",
            state switch
            {
                OverallState.Online => _greenDot,
                OverallState.Starting or OverallState.Degraded => _yellowDot,
                _ => _redDot,
            });
    }

    private void SetComponentStatus(
        int row,
        string name,
        ComponentState state)
    {
        _statusTable.SetRow(
            row,
            $"{name}: {GetComponentText(state)}",
            state switch
            {
                ComponentState.Online => _greenDot,
                ComponentState.Starting => _yellowDot,
                ComponentState.Disabled => _grayDot,
                _ => _redDot,
            });
    }

    private void UpdateSummaryMenu()
    {
        var connections = _lastConnectionCount ?? 0;
        _summaryItem.Text = _lastMcpMetrics is { } metrics
            ? $"MCP {connections} | CPU {metrics.CpuPercent:0.0}% | RAM {FormatMemory(metrics.WorkingSetBytes)}"
            : $"MCP {connections} | CPU -- | RAM --";
        _versionItem.Text =
            $"Version: {GetDisplayVersion()} · Release{_runtime?.ActiveSlot ?? "?"}\\win-x64";
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
        _startRestartItem.Enabled = !busy;
        _stopItem.Enabled = !busy;
        _resetMcpConnectionsItem.Enabled = !busy && _backendOnline;
        _resetReverseSshItem.Enabled = !busy &&
            _runtimeDesiredRunning &&
            _settings.ReverseSsh.Enabled;
        _mappingItem.Enabled = !busy && _settings.ReverseSsh.Enabled;
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
            if (_runtime is not null)
            {
                await _runtime.StopAsync();
                await _runtime.DisposeAsync();
                _runtime = null;
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
            _notifyIcon.Icon = null;
            _timer.Dispose();
            _mcpMetricsTimer.Dispose();
            _metricsHttp.Dispose();
            _notifyIcon.Dispose();
            _generatedTrayIcon?.Dispose();
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

    private void StretchTopLevelItems()
    {
        foreach (ToolStripItem item in _menu.Items)
        {
            if (item is ToolStripSeparator)
            {
                continue;
            }

            var left = Math.Max(0, item.Bounds.Left);
            var width = Math.Max(1, _menu.ClientSize.Width - left - 2);
            var preferredHeight = item.GetPreferredSize(new Size(width, 0)).Height;
            item.AutoSize = false;
            item.Size = new Size(width, Math.Max(22, preferredHeight));

            if (item is ToolStripControlHost host)
            {
                host.Control.AutoSize = false;
                host.Control.Size = new Size(width, Math.Max(1, preferredHeight));
            }
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
            _statusTable.ApplySystemColors();
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
