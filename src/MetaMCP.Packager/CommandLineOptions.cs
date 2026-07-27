namespace MetaMCP.Packager;

internal enum PackageTarget
{
    WinX64,
    LinuxX64,
    LinuxArm64,
}

internal sealed record TargetSpec(
    PackageTarget Target,
    string Id,
    string RuntimeIdentifier,
    bool IsWindows,
    string ExecutableName,
    string NodeArchiveArchitecture);

internal sealed record CommandLineOptions(
    string ProjectRoot,
    string Repository,
    string Output,
    IReadOnlyList<TargetSpec> Targets,
    bool SkipInstall,
    bool SkipSmokeTest,
    bool NormalizeLinksOnly,
    bool ArchiveOnly)
{
    public static CommandLineOptions Parse(string[] args)
    {
        var projectRoot = FindProjectRoot();
        string? repository = null;
        string? output = null;
        var targetName = "win-x64";
        var skipInstall = false;
        var skipSmokeTest = false;
        var normalizeLinksOnly = false;
        var archiveOnly = false;

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
                case "--target":
                    targetName = RequireValue(args, ref index, "--target");
                    break;
                case "--skip-install":
                    skipInstall = true;
                    break;
                case "--skip-smoke-test":
                    skipSmokeTest = true;
                    break;
                case "--normalize-links-only":
                    normalizeLinksOnly = true;
                    break;
                case "--archive-only":
                    archiveOnly = true;
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

        var targets = ParseTargets(targetName);
        repository ??= Path.Combine(Path.GetDirectoryName(projectRoot)!, "metamcp");
        output ??= Path.Combine(projectRoot, DefaultOutputName(targetName));
        if (normalizeLinksOnly &&
            (targets.Count != 1 || !targets[0].IsWindows))
        {
            throw new ArgumentException(
                "--normalize-links-only requires the win-x64 target.");
        }
        if (normalizeLinksOnly && archiveOnly)
        {
            throw new ArgumentException(
                "--normalize-links-only and --archive-only cannot be combined.");
        }

        return new CommandLineOptions(
            projectRoot,
            Path.GetFullPath(repository),
            Path.GetFullPath(output),
            targets,
            skipInstall,
            skipSmokeTest,
            normalizeLinksOnly,
            archiveOnly);
    }

    public string GetOutput(TargetSpec target) =>
        Targets.Count == 1 ? Output : Path.Combine(Output, target.Id);

    private static IReadOnlyList<TargetSpec> ParseTargets(string value) =>
        value.ToLowerInvariant() switch
        {
            "win-x64" => [CreateTarget(PackageTarget.WinX64)],
            "linux-x64" => [CreateTarget(PackageTarget.LinuxX64)],
            "linux-arm64" => [CreateTarget(PackageTarget.LinuxArm64)],
            "all" =>
            [
                CreateTarget(PackageTarget.WinX64),
                CreateTarget(PackageTarget.LinuxX64),
                CreateTarget(PackageTarget.LinuxArm64),
            ],
            _ => throw new ArgumentException(
                $"Unknown target '{value}'. Use win-x64, linux-x64, linux-arm64 or all."),
        };

    private static TargetSpec CreateTarget(PackageTarget target) => target switch
    {
        PackageTarget.WinX64 => new(
            target, "win-x64", "win-x64", true, "MetaMCP.exe", string.Empty),
        PackageTarget.LinuxX64 => new(
            target, "linux-x64", "linux-x64", false, "metamcp-host", "x64"),
        PackageTarget.LinuxArm64 => new(
            target, "linux-arm64", "linux-arm64", false, "metamcp-host", "arm64"),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static string DefaultOutputName(string target) => target.ToLowerInvariant() switch
    {
        "win-x64" => "Release",
        "linux-x64" => "Release-linux-x64",
        "linux-arm64" => "Release-linux-arm64",
        "all" => "Release-Multi",
        _ => "Release",
    };

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
        Console.WriteLine("MetaMCP.Packager [options]");
        Console.WriteLine("  --target win-x64|linux-x64|linux-arm64|all");
        Console.WriteLine("  --repo PATH       MetaMCP source repository");
        Console.WriteLine("  --output PATH     Target directory or root for --target all");
        Console.WriteLine("  --skip-install    Reuse installed pnpm dependencies");
        Console.WriteLine("  --skip-smoke-test Skip executable smoke tests");
        Console.WriteLine("  --normalize-links-only  Normalize an existing single-target release");
        Console.WriteLine("  --archive-only    Archive an existing Linux output without rebuilding");
    }
}
