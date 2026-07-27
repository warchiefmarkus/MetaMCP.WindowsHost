using System.Diagnostics;

namespace MetaMCP.Host;

internal sealed class WindowsRuntimePlatform : IRuntimePlatform
{
    private readonly WindowsJob _job = new();

    public string NodeExecutableRelativePath => Path.Combine("runtime", "node", "node.exe");

    public void AttachProcess(Process process) => _job.Assign(process);

    public bool TryCleanupOrphanedNodeProcess(int port, string name)
    {
        try
        {
            using var check = Process.Start(new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (check is null)
            {
                return false;
            }

            var output = check.StandardOutput.ReadToEnd();
            check.WaitForExit();
            var killed = false;
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port} ") || !line.Contains("LISTENING"))
                {
                    continue;
                }

                var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid) || pid <= 0)
                {
                    continue;
                }

                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    HostLog.Warn($"Killing orphaned {name} process (PID {pid}) occupying port {port}.");
                    process.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }

            return killed;
        }
        catch (Exception ex)
        {
            HostLog.Warn($"Failed to scan for orphaned processes on port {port}: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => _job.Dispose();
}
