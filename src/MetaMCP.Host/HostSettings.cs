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
    public int HealthCheckTimeoutMilliseconds { get; set; } = 1500;
    public int UnhealthyChecksBeforeRestart { get; set; } = 3;
    public int StartupTimeoutSeconds { get; set; } = 90;
    public bool LoggingEnabled { get; set; } = false;
    public bool OpenBrowserOnPortableStart { get; set; } = false;
    public ReverseSshSettings ReverseSsh { get; set; } = new();

    public static HostSettings Load(string baseDirectory)
    {
        var configDirectory = Path.Combine(baseDirectory, "config");
        var path = GetConfigPath(baseDirectory);
        Directory.CreateDirectory(configDirectory);

        if (!File.Exists(path))
        {
            var defaults = new HostSettings();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HostSettings>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize {path}.");
    }

    public void Save(string baseDirectory)
    {
        var path = GetConfigPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static string GetConfigPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "config", "host.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

internal sealed class ReverseSshSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "oracle_freevps2arm";
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int Port { get; set; } = 0;
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public string? Password { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public string RemoteBindHost { get; set; } = "127.0.0.1";
    public uint RemotePort { get; set; } = 18080;
    public string LocalHost { get; set; } = "127.0.0.1";
    public uint LocalPort { get; set; } = 12008;
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int ReconnectDelaySeconds { get; set; } = 10;
}