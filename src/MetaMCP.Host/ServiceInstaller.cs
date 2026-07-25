using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace MetaMCP.Host;

internal static class ServiceInstaller
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsInstalled()
    {
        try
        {
            using var controller = new ServiceController(HostConstants.ServiceName);
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool IsServiceRunning()
    {
        try
        {
            using var controller = new ServiceController(HostConstants.ServiceName);
            return controller.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    public static async Task InstallAsync(string baseDirectory)
    {
        var executable = Application.ExecutablePath;
        var settings = HostSettings.Load(baseDirectory);
        OpenSshConfig.PersistResolvedValues(settings.ReverseSsh);
        settings.Save(baseDirectory);

        var binaryPath = $"\"{executable}\" --service";
        if (IsInstalled())
        {
            await RunScAsync([
                "config",
                HostConstants.ServiceName,
                "binPath=", binaryPath,
                "start=", "delayed-auto",
                "DisplayName=", HostConstants.ServiceDisplayName,
            ]);
        }
        else
        {
            await RunScAsync([
                "create",
                HostConstants.ServiceName,
                "binPath=", binaryPath,
                "start=", "delayed-auto",
                "DisplayName=", HostConstants.ServiceDisplayName,
            ]);
        }

        await RunScAsync([
            "description",
            HostConstants.ServiceName,
            "Hosts MetaMCP frontend, backend and reverse SSH tunnel.",
        ]);
        await RunScAsync([
            "failure",
            HostConstants.ServiceName,
            "reset=", "86400",
            "actions=", "restart/5000/restart/15000/restart/30000",
        ]);

        SetTrayAutoStart(executable, enabled: true);
        var startResult = await RunScAsync(
            ["start", HostConstants.ServiceName],
            throwOnFailure: false);
        if (startResult is not 0 and not 1056)
        {
            throw new InvalidOperationException(
                $"Windows service was installed but could not be started. sc.exe exit code: {startResult}.");
        }

        await WaitForServiceStateAsync(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static async Task UninstallAsync()
    {
        if (IsInstalled())
        {
            var stopResult = await RunScAsync(
                ["stop", HostConstants.ServiceName],
                throwOnFailure: false);
            if (stopResult is 0 or 1062)
            {
                await WaitForServiceStoppedAsync(TimeSpan.FromSeconds(20));
            }

            await RunScAsync(["delete", HostConstants.ServiceName]);
        }

        SetTrayAutoStart(Application.ExecutablePath, enabled: false);
    }

    public static async Task<int> RunElevatedAsync(string command)
    {
        var startInfo = new ProcessStartInfo(Application.ExecutablePath, command)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the elevated MetaMCP helper.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
    }

    private static void SetTrayAutoStart(string executable, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows Run registry key.");
        if (enabled)
        {
            key.SetValue(
                HostConstants.TrayRunValueName,
                $"\"{executable}\" --tray",
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(HostConstants.TrayRunValueName, throwOnMissingValue: false);
        }
    }

    private static async Task<int> RunScAsync(
        IReadOnlyList<string> arguments,
        bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start sc.exe.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sc.exe failed with code {process.ExitCode}.\n{stderr}\n{stdout}".Trim());
        }

        return process.ExitCode;
    }

    private static async Task WaitForServiceStateAsync(
        ServiceControllerStatus state,
        TimeSpan timeout)
    {
        using var controller = new ServiceController(HostConstants.ServiceName);
        await Task.Run(() => controller.WaitForStatus(state, timeout));
    }

    private static async Task WaitForServiceStoppedAsync(TimeSpan timeout)
    {
        try
        {
            await WaitForServiceStateAsync(ServiceControllerStatus.Stopped, timeout);
        }
        catch
        {
        }
    }
}