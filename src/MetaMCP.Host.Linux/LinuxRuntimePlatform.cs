using System.Diagnostics;

namespace MetaMCP.Host;

internal sealed class LinuxRuntimePlatform : IRuntimePlatform
{
    public string NodeExecutableRelativePath =>
        Path.Combine("runtime", "node", "bin", "node");

    public void AttachProcess(Process process)
    {
        // systemd KillMode=control-group owns unexpected process cleanup.
        // RuntimeController still terminates direct process trees on graceful stop.
    }

    public bool TryCleanupOrphanedNodeProcess(int port, string name)
    {
        HostLog.Warn(
            $"Port {port} required by {name} is occupied; Linux host will not kill an unrelated process.");
        return false;
    }

    public void Dispose()
    {
    }
}
