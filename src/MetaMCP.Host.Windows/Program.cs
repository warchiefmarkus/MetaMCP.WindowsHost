namespace MetaMCP.Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var baseDirectory = ResolveBaseDirectory(args);

        try
        {
            return RunTray(baseDirectory);
        }
        catch (Exception ex)
        {
            HostLog.Error("Fatal MetaMCP host error.", ex);
            MessageBox.Show(
                ex.Message,
                "MetaMCP",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunTray(string baseDirectory)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\MetaMCP.WindowsHost.Tray",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext(baseDirectory));
        GC.KeepAlive(mutex);
        return 0;
    }

    private static string ResolveBaseDirectory(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--base", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.GetFullPath(args[i + 1]);
                if (Directory.Exists(path))
                {
                    return path.TrimEnd(Path.DirectorySeparatorChar);
                }
            }
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }
}
