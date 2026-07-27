using System.Runtime.InteropServices;

namespace MetaMCP.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        LinuxOptions options;
        try
        {
            options = LinuxOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            LinuxOptions.PrintHelp();
            return 2;
        }

        if (options.ShowHelp)
        {
            LinuxOptions.PrintHelp();
            return 0;
        }

        var settings = HostSettings.Load(options.BaseDirectory);
        var mappingId = !string.IsNullOrWhiteSpace(options.MappingId)
            ? options.MappingId
            : Environment.GetEnvironmentVariable("METAMCP_MAPPING");
        if (!string.IsNullOrWhiteSpace(mappingId))
        {
            settings.ReverseSsh.ActiveMapping =
                settings.ReverseSsh.GetMapping(mappingId).Id;
        }

        HostLog.Initialize(
            options.BaseDirectory,
            settings.LoggingEnabled,
            consoleEnabled: true);
        HostLog.Info(
            $"Starting MetaMCP Linux host from {options.BaseDirectory}; " +
            $"mapping={settings.ReverseSsh.ActiveMapping}.");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        using var sigTerm = RegisterSignal(PosixSignal.SIGTERM, shutdown);
        using var sigInt = RegisterSignal(PosixSignal.SIGINT, shutdown);

        await using var runtime = new RuntimeController(
            options.BaseDirectory,
            settings,
            new LinuxRuntimePlatform());
        RuntimeStatus? lastStatus = null;
        runtime.StatusChanged += status =>
        {
            if (IsMateriallyDifferent(lastStatus, status))
            {
                HostLog.Info(FormatStatus(status));
                lastStatus = status;
            }
        };

        try
        {
            await runtime.StartAsync(shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            HostLog.Error("Initial MetaMCP startup failed.", ex);
            if (!settings.AutoRestart)
            {
                return 1;
            }

            HostLog.Warn("AutoRestart is enabled; runtime recovery will continue.");
        }

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
        }

        HostLog.Info("Stopping MetaMCP Linux host.");
        return 0;
    }

    private static PosixSignalRegistration? RegisterSignal(
        PosixSignal signal,
        CancellationTokenSource shutdown)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
    }

    private static bool IsMateriallyDifferent(
        RuntimeStatus? previous,
        RuntimeStatus current) =>
        previous is null ||
        previous.Overall != current.Overall ||
        previous.Backend != current.Backend ||
        previous.Frontend != current.Frontend ||
        previous.Database != current.Database ||
        previous.ReverseSsh != current.ReverseSsh ||
        previous.ReverseSshMappingId != current.ReverseSshMappingId ||
        previous.BackendPid != current.BackendPid ||
        previous.FrontendPid != current.FrontendPid ||
        previous.LastError != current.LastError;

    private static string FormatStatus(RuntimeStatus status) =>
        $"Status={status.Overall}; backend={status.Backend}" +
        $"({status.BackendPid?.ToString() ?? "-"}); frontend={status.Frontend}" +
        $"({status.FrontendPid?.ToString() ?? "-"}); database={status.Database}; " +
        $"ssh={status.ReverseSsh}; mapping={status.ReverseSshMappingId ?? "-"}" +
        (string.IsNullOrWhiteSpace(status.LastError)
            ? string.Empty
            : $"; error={status.LastError}");
}

internal sealed record LinuxOptions(
    string BaseDirectory,
    string? MappingId,
    bool ShowHelp)
{
    public static LinuxOptions Parse(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? mappingId = null;
        var showHelp = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--base":
                    baseDirectory = Path.GetFullPath(RequireValue(args, ref index, "--base"));
                    break;
                case "--mapping":
                    mappingId = RequireValue(args, ref index, "--mapping");
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        return new LinuxOptions(
            baseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            mappingId,
            showHelp);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("metamcp-host [--base PATH] [--mapping ID]");
        Console.WriteLine("Runs MetaMCP frontend, backend and reverse SSH tunnel without a GUI.");
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }
}
