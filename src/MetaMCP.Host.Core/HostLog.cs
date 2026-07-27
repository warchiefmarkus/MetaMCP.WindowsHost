namespace MetaMCP.Host;

internal static class HostLog
{
    private static readonly object Sync = new();
    private static string? _path;
    private static bool _fileEnabled;
    private static bool _consoleEnabled;

    public static void Initialize(
        string baseDirectory,
        bool fileEnabled,
        bool consoleEnabled = false)
    {
        _fileEnabled = fileEnabled;
        _consoleEnabled = consoleEnabled;
        if (!fileEnabled)
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
        var text = exception is null
            ? message
            : $"{message}{Environment.NewLine}{exception}";
        Write("ERR", text);
    }

    private static void Write(string level, string message)
    {
        if (!_fileEnabled && !_consoleEnabled)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        lock (Sync)
        {
            if (_consoleEnabled)
            {
                var writer = level == "ERR" ? Console.Error : Console.Out;
                writer.WriteLine(line);
            }

            if (_fileEnabled && _path is not null)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
    }
}
