using System.Security.Cryptography;
using System.Text.Json;

namespace MetaMCP.Host;

internal sealed class HostSettings
{
    public int FrontendPort { get; set; } = 12008;
    public int BackendPort { get; set; } = 12009;
    public string DatabaseHost { get; set; } = "127.0.0.1";
    public int DatabasePort { get; set; } = 5432;
    public bool RunMigrationsOnStart { get; set; } = true;
    public bool AutoStartRuntime { get; set; } = true;
    public bool AutoRestart { get; set; } = true;
    public int RestartDelaySeconds { get; set; } = 5;
    public int HealthCheckIntervalSeconds { get; set; } = 10;
    public int McpMetricsRefreshSeconds { get; set; } = 5;
    public int McpTelemetryTimeoutMilliseconds { get; set; } = 5000;
    public string HostControlToken { get; set; } = string.Empty;
    public int HealthCheckTimeoutMilliseconds { get; set; } = 1500;
    public int UnhealthyChecksBeforeRestart { get; set; } = 3;
    public int StartupTimeoutSeconds { get; set; } = 90;
    public bool LoggingEnabled { get; set; }
    public bool OpenBrowserOnPortableStart { get; set; }
    public ReverseSshSettings ReverseSsh { get; set; } = new();

    public static HostSettings Load(string baseDirectory)
    {
        var configDirectory = Path.Combine(baseDirectory, "config");
        var path = GetConfigPath(baseDirectory);
        Directory.CreateDirectory(configDirectory);
        if (!File.Exists(path))
        {
            var defaults = new HostSettings();
            defaults.Normalize();
            defaults.Save(baseDirectory);
            return defaults;
        }

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<HostSettings>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize {path}.");
        var upgraded = UpgradeLegacyReverseSsh(json, settings.ReverseSsh) ||
            !HasRootProperty(json, nameof(McpMetricsRefreshSeconds)) ||
            !HasRootProperty(json, nameof(McpTelemetryTimeoutMilliseconds)) ||
            !HasRootProperty(json, nameof(HostControlToken)) ||
            string.IsNullOrWhiteSpace(settings.HostControlToken);
        settings.Normalize();
        if (upgraded)
        {
            settings.Save(baseDirectory);
        }
        return settings;
    }

    public void Save(string baseDirectory)
    {
        Normalize();
        var path = GetConfigPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void Normalize()
    {
        HealthCheckIntervalSeconds = Math.Clamp(HealthCheckIntervalSeconds, 2, 3600);
        McpMetricsRefreshSeconds = Math.Clamp(McpMetricsRefreshSeconds, 1, 3600);
        McpTelemetryTimeoutMilliseconds = Math.Clamp(
            McpTelemetryTimeoutMilliseconds,
            1000,
            30000);
        if (string.IsNullOrWhiteSpace(HostControlToken))
        {
            HostControlToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32));
        }
        ReverseSsh.Normalize();
    }
    public static string GetConfigPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "config", "host.json");

    private static bool HasRootProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        return document.RootElement.TryGetProperty(propertyName, out _);
    }

    private static bool UpgradeLegacyReverseSsh(
        string json,
        ReverseSshSettings reverseSsh)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        if (!document.RootElement.TryGetProperty("ReverseSsh", out var legacy) ||
            legacy.ValueKind != JsonValueKind.Object ||
            legacy.TryGetProperty("Mappings", out _))
        {
            return false;
        }

        var remoteBindHost = GetString(legacy, "RemoteBindHost") ?? "127.0.0.1";
        var remotePort = GetUInt32(legacy, "RemotePort") ?? 18080;
        var localHost = GetString(legacy, "LocalHost") ?? "127.0.0.1";
        var localPort = GetUInt32(legacy, "LocalPort") ?? 12008;
        reverseSsh.Mappings = ReverseSshSettings.CreateDefaultMappings(
            remoteBindHost,
            remotePort == 18082 ? 18080 : remotePort,
            localHost,
            localPort);
        if (remotePort == 18082)
        {
            var thinkPad = reverseSsh.Mappings.First(mapping =>
                mapping.Id.Equals("thinkpad", StringComparison.OrdinalIgnoreCase));
            thinkPad.RemoteBindHost = remoteBindHost;
            thinkPad.RemotePort = remotePort;
            thinkPad.LocalHost = localHost;
            thinkPad.LocalPort = localPort;
            reverseSsh.ActiveMapping = "thinkpad";
        }
        else
        {
            reverseSsh.ActiveMapping = "legion";
        }
        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    private static uint? GetUInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var number)
            ? number
            : null;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
}

internal sealed class ReverseSshSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "oracle_freevps2arm";
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int Port { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public string? Password { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public string ActiveMapping { get; set; } = "legion";
    public List<ReverseSshMappingSettings> Mappings { get; set; } = CreateDefaultMappings();
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int ReconnectDelaySeconds { get; set; } = 10;

    public ReverseSshMappingSettings GetActiveMapping()
    {
        Normalize();
        return Mappings.First(mapping =>
            mapping.Id.Equals(ActiveMapping, StringComparison.OrdinalIgnoreCase));
    }

    public ReverseSshMappingSettings GetMapping(string mappingId)
    {
        Normalize();
        return Mappings.FirstOrDefault(mapping =>
                mapping.Id.Equals(mappingId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Unknown reverse SSH mapping: {mappingId}.");
    }

    public void Normalize()
    {
        Mappings ??= [];
        if (Mappings.Count == 0)
        {
            Mappings = CreateDefaultMappings();
        }

        foreach (var mapping in Mappings)
        {
            mapping.Normalize();
        }
        var duplicateId = Mappings
            .GroupBy(mapping => mapping.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidDataException(
                $"Duplicate reverse SSH mapping id: {duplicateId.Key}.");
        }

        var duplicatePort = Mappings
            .GroupBy(mapping => mapping.RemotePort)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePort is not null)
        {
            throw new InvalidDataException(
                $"Duplicate reverse SSH remote port: {duplicatePort.Key}.");
        }

        if (!Mappings.Any(mapping =>
                mapping.Id.Equals(ActiveMapping, StringComparison.OrdinalIgnoreCase)))
        {
            ActiveMapping = Mappings[0].Id;
        }
        else
        {
            ActiveMapping = Mappings.First(mapping =>
                mapping.Id.Equals(ActiveMapping, StringComparison.OrdinalIgnoreCase)).Id;
        }
    }

    public static List<ReverseSshMappingSettings> CreateDefaultMappings(
        string remoteBindHost = "127.0.0.1",
        uint legionRemotePort = 18080,
        string localHost = "127.0.0.1",
        uint localPort = 12008) =>
    [
        new()
        {
            Id = "legion",
            DisplayName = "Legion PC",
            PublicPath = "/metamcp",
            RemoteBindHost = remoteBindHost,
            RemotePort = legionRemotePort,
            LocalHost = localHost,
            LocalPort = localPort,
        },
        new()
        {
            Id = "thinkpad",
            DisplayName = "ThinkPad",
            PublicPath = "/metamcpthp",
            RemoteBindHost = "127.0.0.1",
            RemotePort = 18082,
            LocalHost = localHost,
            LocalPort = localPort,
        },
    ];
}

internal sealed class ReverseSshMappingSettings
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PublicPath { get; set; } = string.Empty;
    public string RemoteBindHost { get; set; } = "127.0.0.1";
    public uint RemotePort { get; set; }
    public string LocalHost { get; set; } = "127.0.0.1";
    public uint LocalPort { get; set; } = 12008;
    public void Normalize()
    {
        Id = Id.Trim();
        DisplayName = DisplayName.Trim();
        PublicPath = PublicPath.Trim();
        RemoteBindHost = RemoteBindHost.Trim();
        LocalHost = LocalHost.Trim();

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new InvalidDataException("Reverse SSH mapping id is required.");
        }
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            DisplayName = Id;
        }
        if (string.IsNullOrWhiteSpace(PublicPath))
        {
            throw new InvalidDataException($"PublicPath is required for mapping {Id}.");
        }
        PublicPath = "/" + PublicPath.Trim('/');
        if (RemotePort == 0 || LocalPort == 0)
        {
            throw new InvalidDataException(
                $"RemotePort and LocalPort must be greater than zero for mapping {Id}.");
        }
    }
}
