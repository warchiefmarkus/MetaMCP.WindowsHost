using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetaMCP.Host;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var baseDirectory = ResolveBaseDirectory(args);
        var command = args.FirstOrDefault()?.ToLowerInvariant();

        try
        {
            return command switch
            {
                "--service" => await RunServiceAsync(baseDirectory, args),
                "--install-service" => await RunInstallAsync(baseDirectory),
                "--uninstall-service" => await RunUninstallAsync(),
                _ => RunTray(baseDirectory),
            };
        }
        catch (Exception ex)
        {
            HostLog.Error("Fatal MetaMCP host error.", ex);
            if (command != "--service")
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

    private static async Task<int> RunServiceAsync(string baseDirectory, string[] args)
    {
        var settings = HostSettings.Load(baseDirectory);
        HostLog.Initialize(baseDirectory, settings.LoggingEnabled);

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = HostConstants.ServiceName;
        });
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(
            _ => new RuntimeController(baseDirectory, settings, new WindowsRuntimePlatform()));
        builder.Services.AddSingleton<PipeServer>();
        builder.Services.AddHostedService<ServiceWorker>();

        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    private static async Task<int> RunInstallAsync(string baseDirectory)
    {
        await ServiceInstaller.InstallAsync(baseDirectory);
        return 0;
    }

    private static async Task<int> RunUninstallAsync()
    {
        await ServiceInstaller.UninstallAsync();
        return 0;
    }
}
