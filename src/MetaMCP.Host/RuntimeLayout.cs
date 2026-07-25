namespace MetaMCP.Host;

internal sealed class RuntimeLayout
{
    public RuntimeLayout(string baseDirectory)
    {
        BaseDirectory = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        ConfigDirectory = Path.Combine(BaseDirectory, "config");
        DataDirectory = Path.Combine(BaseDirectory, "data");
        NodeExecutable = Path.Combine(BaseDirectory, "runtime", "node", "node.exe");
        BackendDirectory = Path.Combine(BaseDirectory, "metamcp", "backend");
        FrontendDirectory = Path.Combine(BaseDirectory, "metamcp", "frontend");
        BackendEntry = Path.Combine(BackendDirectory, "dist", "index.js");
        FrontendEntry = Path.Combine(FrontendDirectory, "server.js");
        MigrationEntry = Path.Combine(BackendDirectory, "scripts", "migrate.mjs");
        EnvironmentFile = Path.Combine(ConfigDirectory, ".env.local");
        RunnerDirectory = Path.Combine(DataDirectory, "mcp-runners", "default");
    }

    public string BaseDirectory { get; }
    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string NodeExecutable { get; }
    public string BackendDirectory { get; }
    public string FrontendDirectory { get; }
    public string BackendEntry { get; }
    public string FrontendEntry { get; }
    public string MigrationEntry { get; }
    public string EnvironmentFile { get; }
    public string RunnerDirectory { get; }

    public void Validate()
    {
        RequireFile(NodeExecutable);
        RequireFile(BackendEntry);
        RequireFile(FrontendEntry);
        RequireFile(EnvironmentFile);

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(RunnerDirectory);
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required MetaMCP runtime file is missing.", path);
        }
    }
}