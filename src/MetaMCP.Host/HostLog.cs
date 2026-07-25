namespace MetaMCP.Host;

internal static class HostLog
{
    private static readonly object Sync = new();
    private static string? _path;
    private static bool _enabled;

    public static void Initialize(string baseDirectory, bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _path = null;
            return;
        }

        var directory = Path.Combine(baseDirectory, "logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "host.log");
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERR", text);
    }

    private static void Write(string level, string message)
    {
        if (!_enabled || _path is null)
        {
            return;
        }

        lock (Sync)
        {
            File.AppendAllText(
                _path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
    }
}