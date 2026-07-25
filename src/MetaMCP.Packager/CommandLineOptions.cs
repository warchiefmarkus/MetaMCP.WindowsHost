namespace MetaMCP.Packager;

internal sealed record CommandLineOptions(
    string ProjectRoot,
    string Repository,
    string Output,
    bool SkipInstall,
    bool NormalizeLinksOnly)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var projectRoot = FindProjectRoot();
        string? repository = null;
        string? output = null;
        var skipInstall = false;
        var normalizeLinksOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "--repo":
                    repository = RequireValue(args, ref index, "--repo");
                    break;
                case "--output":
                    output = RequireValue(args, ref index, "--output");
                    break;
                case "--skip-install":
                    skipInstall = true;
                    break;
                case "--normalize-links-only":
                    normalizeLinksOnly = true;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        repository ??= Path.Combine(Path.GetDirectoryName(projectRoot)!, "metamcp");
        output ??= Path.Combine(projectRoot, "Release");
        return new CommandLineOptions(
            projectRoot,
            Path.GetFullPath(repository),
            Path.GetFullPath(output),
            skipInstall,
            normalizeLinksOnly);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }
        return args[index];
    }

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (directory.EnumerateFiles("MetaMCP.WindowsHost.sln*").Any())
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the MetaMCP.WindowsHost solution directory.");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            "MetaMCP.Packager [--repo PATH] [--output PATH] [--skip-install] [--normalize-links-only]");
        Console.WriteLine("Builds an autonomous Windows MetaMCP production release.");
        Console.WriteLine(
            "--normalize-links-only converts an existing release to portable relative links without rebuilding it.");
    }
}