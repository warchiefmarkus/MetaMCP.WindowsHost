using System.Diagnostics;

namespace MetaMCP.Host;

internal interface IRuntimePlatform : IDisposable
{
    string NodeExecutableRelativePath { get; }
    void AttachProcess(Process process);
    bool TryCleanupOrphanedNodeProcess(int port, string name);
}
