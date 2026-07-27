using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MetaMCP.Packager;

internal sealed class MultiPlatformPackager
{
    private readonly CommandLineOptions _options;
    private readonly string _node;
    private readonly string _dotnet;
    private readonly string _tar;
    private readonly string _linuxProject;
    private readonly string _cacheDirectory;
    private string? _nodeVersion;

    public MultiPlatformPackager(CommandLineOptions options)
    {
        _options = options;
        _node = ProcessRunner.FindExecutable("node.exe", "node");
        _dotnet = ProcessRunner.FindExecutable("dotnet.exe", "dotnet");
        _tar = ProcessRunner.FindExecutable("tar.exe", "tar");
        _linuxProject = Path.Combine(
            options.ProjectRoot,
            "src",
            "MetaMCP.Host.Linux",
            "MetaMCP.Host.Linux.csproj");
        _cacheDirectory = Path.Combine(options.ProjectRoot, ".node-runtime-cache");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ArchiveOnly)
        {
            CreateArchiveForExistingOutput();
            return;
        }

        if (_options.NormalizeLinksOnly)
        {
            await new ReleasePackager(_options).RunAsync(cancellationToken);
            return;
        }

        var windowsTarget = _options.Targets.FirstOrDefault(target => target.IsWindows);
        var linuxTargets = _options.Targets.Where(target => !target.IsWindows).ToArray();
        var temporarySource = windowsTarget is null;
        var sourceRelease = temporarySource
            ? Path.Combine(_options.ProjectRoot, ".linux-app-staging")
            : _options.GetOutput(windowsTarget!);

        try
        {
            var sourceOptions = CreateWindowsSourceOptions(sourceRelease);
            await new ReleasePackager(sourceOptions).RunAsync(cancellationToken);

            foreach (var target in linuxTargets)
            {
                await PackageLinuxAsync(sourceRelease, target, cancellationToken);
            }
        }
        finally
        {
            if (temporarySource)
            {
                FileSystemUtil.DeleteDirectory(sourceRelease);
            }
        }
    }

    private void CreateArchiveForExistingOutput()
    {
        if (_options.Targets.Count != 1 || _options.Targets[0].IsWindows)
        {
            throw new ArgumentException(
                "--archive-only requires one Linux target.");
        }

        var output = _options.GetOutput(_options.Targets[0]);
        ValidateLinuxRelease(_options.Targets[0], output);
        var archive = output.TrimEnd(Path.DirectorySeparatorChar) + ".tar.gz";
        PortableTarArchive.CreateFromDirectory(output, archive);
        Console.WriteLine($"Archive: {archive}");
    }

    private CommandLineOptions CreateWindowsSourceOptions(string output)
    {
        var win = _options.Targets.FirstOrDefault(target => target.IsWindows)
            ?? new TargetSpec(
                PackageTarget.WinX64,
                "win-x64",
                "win-x64",
                true,
                "MetaMCP.exe",
                string.Empty);
        return new CommandLineOptions(
            _options.ProjectRoot,
            _options.Repository,
            output,
            [win],
            _options.SkipInstall,
            _options.SkipSmokeTest,
            false,
            false);
    }

    private async Task PackageLinuxAsync(
        string sourceRelease,
        TargetSpec target,
        CancellationToken cancellationToken)
    {
        var output = _options.GetOutput(target);
        EnsureReleaseIsNotRunning(output);
        var preservedConfig = ReadOptional(Path.Combine(output, "config", "host.json"));
        var backup = PreserveData(output, target.Id);
        FileSystemUtil.RecreateDirectory(output);

        try
        {
            CopyPortableApplication(sourceRelease, output, preservedConfig, backup);
            await CopyLinuxNodeRuntimeAsync(target, output, cancellationToken);
            await PublishLinuxHostAsync(target, output, cancellationToken);
            WriteLinuxDeploymentFiles(output);
            await WriteManifestAsync(target, output, cancellationToken);
            ValidateLinuxRelease(target, output);
            var archive = CreateArchive(output);
            PrintSuccess(target, output, archive);
        }
        finally
        {
            if (backup is not null)
            {
                FileSystemUtil.DeleteDirectory(backup);
            }
        }
    }

    private string? PreserveData(string output, string targetId)
    {
        var source = Path.Combine(output, "data");
        if (!Directory.Exists(source))
        {
            return null;
        }

        var backup = Path.Combine(
            _options.ProjectRoot,
            ".packager-data-backup",
            targetId);
        FileSystemUtil.DeleteDirectory(backup);
        FileSystemUtil.CopyDirectory(source, backup);
        return backup;
    }

    private void CopyPortableApplication(
        string sourceRelease,
        string output,
        string? preservedConfig,
        string? backup)
    {
        FileSystemUtil.CopyDirectoryPreserveLinks(
            Path.Combine(sourceRelease, "metamcp"),
            Path.Combine(output, "metamcp"));
        FileSystemUtil.CopyFile(
            Path.Combine(sourceRelease, "README.md"),
            Path.Combine(output, "README.md"));
        var config = Path.Combine(output, "config");
        Directory.CreateDirectory(config);
        FileSystemUtil.CopyFile(
            Path.Combine(sourceRelease, "config", ".env.local"),
            Path.Combine(config, ".env.local"));
        File.WriteAllText(
            Path.Combine(config, "host.json"),
            preservedConfig ?? File.ReadAllText(
                Path.Combine(sourceRelease, "config", "host.json")));

        if (backup is not null)
        {
            FileSystemUtil.CopyDirectory(backup, Path.Combine(output, "data"));
        }
        Directory.CreateDirectory(Path.Combine(output, "data", "mcp-runners", "default"));
    }

    private async Task CopyLinuxNodeRuntimeAsync(
        TargetSpec target,
        string output,
        CancellationToken cancellationToken)
    {
        var cache = await PrepareLinuxNodeRuntimeAsync(target, cancellationToken);
        FileSystemUtil.CopyDirectoryPreserveLinks(
            cache,
            Path.Combine(output, "runtime", "node"));
    }

    private async Task<string> PrepareLinuxNodeRuntimeAsync(
        TargetSpec target,
        CancellationToken cancellationToken)
    {
        var version = await GetNodeVersionAsync(cancellationToken);
        var cache = Path.Combine(_cacheDirectory, $"v{version}", target.Id);
        var node = Path.Combine(cache, "bin", "node");
        if (File.Exists(node))
        {
            return cache;
        }

        FileSystemUtil.RecreateDirectory(cache);
        var fileName =
            $"node-v{version}-linux-{target.NodeArchiveArchitecture}.tar.xz";
        var downloadDirectory = Path.Combine(
            _cacheDirectory,
            "downloads",
            $"v{version}");
        Directory.CreateDirectory(downloadDirectory);
        var archive = Path.Combine(downloadDirectory, fileName);
        await DownloadNodeArchiveAsync(
            version,
            fileName,
            archive,
            cancellationToken);

        var extract = Path.Combine(_cacheDirectory, "extract", target.Id);
        FileSystemUtil.RecreateDirectory(extract);
        try
        {
            await ProcessRunner.RunAsync(
                _tar,
                ["-xf", archive, "-C", extract],
                _options.ProjectRoot,
                cancellationToken: cancellationToken);
            var extractedRoot = Directory.EnumerateDirectories(extract).Single();
            FileSystemUtil.CopyDirectory(extractedRoot, cache);
        }
        finally
        {
            FileSystemUtil.DeleteDirectory(extract);
        }

        FileSystemUtil.RequireFile(node);
        return cache;
    }

    private async Task DownloadNodeArchiveAsync(
        string version,
        string fileName,
        string archive,
        CancellationToken cancellationToken)
    {
        var baseUrl = $"https://nodejs.org/dist/v{version}";
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        var sumsPath = Path.Combine(
            Path.GetDirectoryName(archive)!,
            "SHASUMS256.txt");
        if (!File.Exists(sumsPath))
        {
            File.WriteAllText(
                sumsPath,
                await http.GetStringAsync(
                    $"{baseUrl}/SHASUMS256.txt",
                    cancellationToken));
        }

        if (!File.Exists(archive))
        {
            Console.WriteLine($"Downloading {fileName}...");
            await using var source = await http.GetStreamAsync(
                $"{baseUrl}/{fileName}",
                cancellationToken);
            await using var destination = File.Create(archive);
            await source.CopyToAsync(destination, cancellationToken);
        }

        var expected = File.ReadLines(sumsPath)
            .Select(line => line.Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(parts =>
                parts.Length >= 2 && parts[^1] == fileName)?[0]
            ?? throw new InvalidDataException(
                $"SHA-256 for {fileName} was not found.");
        await using var stream = File.OpenRead(archive);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(archive);
            throw new InvalidDataException(
                $"Node.js archive checksum mismatch for {fileName}.");
        }
    }

    private async Task<string> GetNodeVersionAsync(
        CancellationToken cancellationToken)
    {
        if (_nodeVersion is not null)
        {
            return _nodeVersion;
        }

        var result = await ProcessRunner.RunAsync(
            _node,
            ["--version"],
            _options.Repository,
            cancellationToken: cancellationToken);
        _nodeVersion = result.Output.Trim().TrimStart('v');
        return _nodeVersion;
    }

    private async Task PublishLinuxHostAsync(
        TargetSpec target,
        string output,
        CancellationToken cancellationToken)
    {
        var publish = Path.Combine(
            _options.ProjectRoot,
            ".host-publish",
            target.Id);
        FileSystemUtil.RecreateDirectory(publish);
        await ProcessRunner.RunAsync(
            _dotnet,
            [
                "publish",
                _linuxProject,
                "-c", "Release",
                "-r", target.RuntimeIdentifier,
                "--self-contained", "true",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:PublishReadyToRun=false",
                "-p:DebugType=None",
                "-o", publish,
            ],
            _options.ProjectRoot,
            cancellationToken: cancellationToken);
        FileSystemUtil.CopyFile(
            Path.Combine(publish, target.ExecutableName),
            Path.Combine(output, target.ExecutableName));
    }

    private static void WriteLinuxDeploymentFiles(string output)
    {
        var deploy = Path.Combine(output, "deploy");
        Directory.CreateDirectory(deploy);
        File.WriteAllText(
            Path.Combine(deploy, "metamcp-host.service"),
            "[Unit]\n" +
            "Description=MetaMCP Linux Host\n" +
            "After=network-online.target postgresql.service\n" +
            "Wants=network-online.target\n\n" +
            "[Service]\n" +
            "Type=simple\n" +
            "User=root\n" +
            "WorkingDirectory=__METAMCP_HOME__\n" +
            "ExecStart=__METAMCP_HOME__/metamcp-host --base __METAMCP_HOME__\n" +
            "Restart=always\n" +
            "RestartSec=5\n" +
            "KillMode=control-group\n" +
            "TimeoutStopSec=30\n\n" +
            "[Install]\n" +
            "WantedBy=multi-user.target\n");

        File.WriteAllText(
            Path.Combine(deploy, "install-systemd.sh"),
            "#!/usr/bin/env bash\n" +
            "set -Eeuo pipefail\n" +
            "SOURCE_DIR=\"$(cd \"$(dirname \"${BASH_SOURCE[0]}\")/..\" && pwd)\"\n" +
            "TARGET_DIR=\"${1:-/opt/metamcp}\"\n" +
            "sudo mkdir -p \"$TARGET_DIR\"\n" +
            "if [[ \"$SOURCE_DIR\" != \"$TARGET_DIR\" ]]; then sudo cp -a \"$SOURCE_DIR/.\" \"$TARGET_DIR/\"; fi\n" +
            "sudo find \"$TARGET_DIR\" -type d -exec chmod 755 {} +\n" +
            "sudo find \"$TARGET_DIR\" -type f -exec chmod 644 {} +\n" +
            "sudo chmod +x \"$TARGET_DIR/metamcp-host\" \"$TARGET_DIR/deploy/install-systemd.sh\"\n" +
            "sudo chmod +x \"$TARGET_DIR/runtime/node/bin/\"* 2>/dev/null || true\n" +
            "sed \"s|__METAMCP_HOME__|$TARGET_DIR|g\" \"$TARGET_DIR/deploy/metamcp-host.service\" | sudo tee /etc/systemd/system/metamcp-host.service >/dev/null\n" +
            "sudo systemctl daemon-reload\n" +
            "sudo systemctl enable --now metamcp-host.service\n" +
            "sudo systemctl status metamcp-host.service --no-pager\n");
    }

    private async Task WriteManifestAsync(
        TargetSpec target,
        string output,
        CancellationToken cancellationToken)
    {
        var nodeVersion = await GetNodeVersionAsync(cancellationToken);
        string? gitCommit = null;
        try
        {
            var git = ProcessRunner.FindExecutable("git.exe", "git.cmd", "git");
            var result = await ProcessRunner.RunAsync(
                git,
                ["rev-parse", "--short", "HEAD"],
                _options.Repository,
                throwOnFailure: false,
                cancellationToken: cancellationToken);
            gitCommit = result.Output.Trim();
        }
        catch
        {
        }

        var manifest = new
        {
            builtAt = DateTimeOffset.Now,
            target = target.Id,
            runtimeIdentifier = target.RuntimeIdentifier,
            sourceRepository = _options.Repository,
            gitCommit,
            nodeVersion = $"v{nodeVersion}",
            frontendPort = 12008,
            backendPort = 12009,
        };
        File.WriteAllText(
            Path.Combine(output, "build-manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateLinuxRelease(
        TargetSpec target,
        string output)
    {
        foreach (var file in new[]
        {
            Path.Combine(output, target.ExecutableName),
            Path.Combine(output, "runtime", "node", "bin", "node"),
            Path.Combine(output, "runtime", "node", "bin", "npm"),
            Path.Combine(output, "runtime", "node", "bin", "npx"),
            Path.Combine(output, "metamcp", "backend", "dist", "index.js"),
            Path.Combine(output, "metamcp", "frontend", "server.js"),
            Path.Combine(output, "config", ".env.local"),
            Path.Combine(output, "config", "host.json"),
            Path.Combine(output, "deploy", "metamcp-host.service"),
            Path.Combine(output, "deploy", "install-systemd.sh"),
        })
        {
            FileSystemUtil.RequireFile(file);
        }
        RequireElf(Path.Combine(output, target.ExecutableName));
        RequireElf(Path.Combine(output, "runtime", "node", "bin", "node"));

        var broken = FileSystemUtil.GetBrokenReparsePoints(output);
        if (broken.Count > 0)
        {
            throw new InvalidOperationException(
                "Linux release contains broken links:\n" +
                string.Join('\n', broken.Take(20)));
        }
        var nonPortable = FileSystemUtil.GetNonPortableReparsePoints(output);
        if (nonPortable.Count > 0)
        {
            throw new InvalidOperationException(
                "Linux release contains non-portable links:\n" +
                string.Join('\n', nonPortable.Take(20)));
        }
    }

    private static void RequireElf(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        if (stream.Read(header) != 4 ||
            header[0] != 0x7F ||
            header[1] != (byte)'E' ||
            header[2] != (byte)'L' ||
            header[3] != (byte)'F')
        {
            throw new InvalidDataException(
                $"Expected an ELF executable: {path}");
        }
    }

    private static string CreateArchive(string output)
    {
        var archive = output.TrimEnd(Path.DirectorySeparatorChar) + ".tar.gz";
        PortableTarArchive.CreateFromDirectory(output, archive);
        FileSystemUtil.RequireFile(archive);
        return archive;
    }

    private static void EnsureReleaseIsNotRunning(string output)
    {
        if (!Directory.Exists(output))
        {
            return;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (executable is not null &&
                    executable.StartsWith(
                        output.TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Release process PID {process.Id} is running: {executable}");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string? ReadOptional(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static void PrintSuccess(
        TargetSpec target,
        string output,
        string archive)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine($"{target.Id} package completed.");
        Console.WriteLine($"Directory: {output}");
        Console.WriteLine($"Archive:   {archive}");
        Console.WriteLine(
            $"Size:      {FormatBytes(FileSystemUtil.GetDirectorySize(output))}");
        Console.ResetColor();
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
