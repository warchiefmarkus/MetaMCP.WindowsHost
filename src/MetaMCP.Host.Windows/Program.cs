using System.Security.Cryptography;
using System.Text;

namespace MetaMCP.Host;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var baseDirectory = ResolveBaseDirectory(args);
        var runtimeIndex = Array.FindIndex(
            args,
            value => value.Equals("runtime", StringComparison.OrdinalIgnoreCase));

        // Bootstrap/lifecycle diagnostics must stay available even when the
        // optional verbose runtime logging setting is disabled.
        HostLog.Initialize(baseDirectory, fileEnabled: true);
        HostLog.Info(
            $"MetaMCP process starting. PID={Environment.ProcessId}; EXE={Application.ExecutablePath}; args={string.Join(' ', args)}.");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception
                ?? new Exception(eventArgs.ExceptionObject?.ToString() ?? "Unknown unhandled exception.");
            HostLog.Error(
                $"Unhandled AppDomain exception. terminating={eventArgs.IsTerminating}.",
                exception);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            HostLog.Error("Unobserved task exception.", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            HostLog.Info($"MetaMCP process exit. PID={Environment.ProcessId}.");

        try
        {
            if (runtimeIndex >= 0)
            {
                return await RuntimeControlClient.RunAsync(
                    baseDirectory,
                    args.Skip(runtimeIndex + 1).ToArray());
            }

            return RunTray(baseDirectory);
        }
        catch (Exception ex)
        {
            HostLog.Error("Fatal MetaMCP host error.", ex);
            if (runtimeIndex >= 0)
            {
                Console.Error.WriteLine(ex.Message);
            }
            else
            {
                MessageBox.Show(
                    ex.Message,
                    "MetaMCP",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static int RunTray(string baseDirectory)
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: BuildMutexName(baseDirectory),
            createdNew: out var createdNew);
        if (!createdNew)
        {
            HostLog.Info("Another MetaMCP tray instance already owns the bootstrapper mutex; exiting duplicate process.");
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) =>
            HostLog.Error(
                "Unhandled WinForms UI-thread exception was caught; keeping the bootstrapper alive.",
                eventArgs.Exception);

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(baseDirectory);
        Application.Run(context);
        HostLog.Info("WinForms message loop returned normally.");
        GC.KeepAlive(mutex);
        return 0;
    }

    private static string BuildMutexName(string baseDirectory)
    {
        var normalized = Path.GetFullPath(baseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return $@"Local\MetaMCP.WindowsHost.Tray.{hash}";
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
