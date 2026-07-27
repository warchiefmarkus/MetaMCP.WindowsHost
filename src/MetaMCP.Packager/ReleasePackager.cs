using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MetaMCP.Packager;

internal sealed class ReleasePackager
{
    private readonly CommandLineOptions _options;
    private readonly string _pnpm;
    private readonly string _node;
    private readonly string _dotnet;
    private readonly string _assetsDirectory;
    private readonly string _hostProject;

    public ReleasePackager(CommandLineOptions options)
    {
        _options = options;
        _pnpm = ProcessRunner.FindExecutable("pnpm.cmd", "pnpm.exe");
        _node = ProcessRunner.FindExecutable("node.exe");
        _dotnet = ProcessRunner.FindExecutable("dotnet.exe");
        _assetsDirectory = Path.Combine(
            options.ProjectRoot,
            "src",
            "MetaMCP.Packager",
            "assets");
        _hostProject = Path.Combine(
            options.ProjectRoot,
            "src",
            "MetaMCP.Host.Windows",
            "MetaMCP.Host.Windows.csproj");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        EnsureReleaseIsNotRunning();
        if (_options.NormalizeLinksOnly)
        {
            if (!Directory.Exists(_options.Output))
            {
                throw new DirectoryNotFoundException(
                    $"Release directory not found: {_options.Output}");
            }

            Heading("Making release links portable");
            var convertedLinks = FileSystemUtil.MakeReparsePointsPortable(_options.Output);
            Console.WriteLine($"Converted {convertedLinks} internal junctions to relative symbolic links.");
            ValidateRelease();
            Console.WriteLine("Release links are portable and valid.");
            return;
        }

        ValidateInputs();
        var preservedHostConfig = ReadOptional(
            Path.Combine(_options.Output, "config", "host.json"));
        var dataBackup = Path.Combine(_options.ProjectRoot, ".packager-data-backup");
        FileSystemUtil.DeleteDirectory(dataBackup);
        var existingData = Path.Combine(_options.Output, "data");
        if (Directory.Exists(existingData))
        {
            FileSystemUtil.CopyDirectory(existingData, dataBackup);
        }

        FileSystemUtil.RecreateDirectory(_options.Output);
        try
        {
            var buildEnvironment = LoadEnvironment(
                Path.Combine(_options.Repository, ".env.local"));

            if (!_options.SkipInstall)
            {
                Heading("Installing workspace dependencies");
                await RunPnpmAsync(
                    ["install", "--no-frozen-lockfile"],
                    buildEnvironment,
                    cancellationToken);
            }

            Heading("Building MetaMCP production artifacts");
            await RunPnpmAsync(["--filter", "@repo/zod-types", "build"], null, cancellationToken);
            await RunPnpmAsync(["--filter", "@repo/trpc", "build"], null, cancellationToken);
            await RunPnpmAsync(["--filter", "backend", "build"], buildEnvironment, cancellationToken);
            await RunPnpmAsync(["--filter", "frontend", "check-types"], buildEnvironment, cancellationToken);

            var frontendNext = Path.Combine(_options.Repository, "apps", "frontend", ".next");
            FileSystemUtil.DeleteDirectory(frontendNext);
            await RunPnpmAsync(["--filter", "frontend", "build"], buildEnvironment, cancellationToken);

            Heading("Packaging backend");
            var backendDestination = Path.Combine(_options.Output, "metamcp", "backend");
            await RunPnpmAsync(
                ["--filter", "backend", "--prod", "deploy", backendDestination],
                null,
                cancellationToken);
            CopyWorkspaceRuntime(backendDestination);
            var backendScripts = Path.Combine(backendDestination, "scripts");
            Directory.CreateDirectory(backendScripts);
            FileSystemUtil.CopyFile(
                Path.Combine(_assetsDirectory, "migrate.mjs"),
                Path.Combine(backendScripts, "migrate.mjs"));

            Heading("Packaging frontend");
            var frontendDestination = Path.Combine(_options.Output, "metamcp", "frontend");
            await RunPnpmAsync(
                ["--filter", "frontend", "--prod", "deploy", frontendDestination],
                null,
                cancellationToken);
            OverlayNextStandalone(frontendDestination);
            CopyWorkspaceRuntime(frontendDestination);

            Heading("Packaging Node.js runtime");
            CopyNodeRuntime();

            Heading("Publishing MetaMCP Windows host");
            await PublishHostAsync(cancellationToken);

            Heading("Writing configuration and data");
            WriteConfiguration(preservedHostConfig);
            FileSystemUtil.CopyFile(
                Path.Combine(_options.ProjectRoot, "README.md"),
                Path.Combine(_options.Output, "README.md"));
            if (Directory.Exists(dataBackup))
            {
                FileSystemUtil.CopyDirectory(dataBackup, Path.Combine(_options.Output, "data"));
            }
            Directory.CreateDirectory(Path.Combine(_options.Output, "data", "mcp-runners", "default"));

            await WriteManifestAsync(cancellationToken);

            Heading("Making release links portable");
            var convertedLinks = FileSystemUtil.MakeReparsePointsPortable(_options.Output);
            Console.WriteLine($"Converted {convertedLinks} internal junctions to relative symbolic links.");
            ValidateRelease();

            if (!_options.SkipSmokeTest)
            {
                Heading("Running packaged smoke tests");
                await SmokeTestAsync(cancellationToken);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine();
            Console.WriteLine("MetaMCP Windows release completed successfully.");
            Console.WriteLine($"Executable: {Path.Combine(_options.Output, "MetaMCP.exe")}");
            Console.WriteLine($"Size:       {FormatBytes(FileSystemUtil.GetDirectorySize(_options.Output))}");
            Console.ResetColor();
        }
        finally
        {
            FileSystemUtil.DeleteDirectory(dataBackup);
            FileSystemUtil.DeleteDirectory(Path.Combine(_options.ProjectRoot, ".host-publish"));
        }
    }

    private async Task RunPnpmAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync(
            _pnpm,
            arguments,
            _options.Repository,
            environment,
            cancellationToken: cancellationToken);
    }

    private void OverlayNextStandalone(string frontendDestination)
    {
        var standaloneRoot = Path.Combine(
            _options.Repository,
            "apps",
            "frontend",
            ".next",
            "standalone");
        if (!Directory.Exists(standaloneRoot))
        {
            throw new DirectoryNotFoundException(
                $"Next.js standalone output is missing: {standaloneRoot}");
        }

        var server = Directory.EnumerateFiles(
                standaloneRoot,
                "server.js",
                SearchOption.AllDirectories)
            .FirstOrDefault(path => Normalize(path).EndsWith(
                "/apps/frontend/server.js",
                StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            throw new FileNotFoundException(
                "Could not locate apps/frontend/server.js in Next.js standalone output.");
        }

        var standaloneApp = Path.GetDirectoryName(server)!;
        FileSystemUtil.CopyFile(server, Path.Combine(frontendDestination, "server.js"));
        FileSystemUtil.CopyDirectory(
            Path.Combine(standaloneApp, ".next"),
            Path.Combine(frontendDestination, ".next"));
        FileSystemUtil.CopyDirectory(
            Path.Combine(_options.Repository, "apps", "frontend", ".next", "static"),
            Path.Combine(frontendDestination, ".next", "static"));

        var publicDirectory = Path.Combine(_options.Repository, "apps", "frontend", "public");
        if (Directory.Exists(publicDirectory))
        {
            FileSystemUtil.CopyDirectory(
                publicDirectory,
                Path.Combine(frontendDestination, "public"));
        }
    }

    private void CopyWorkspaceRuntime(string packageDestination)
    {
        var packages = new[]
        {
            (Source: Path.Combine(_options.Repository, "packages", "zod-types", "dist"),
             Target: Path.Combine(packageDestination, "node_modules", "@repo", "zod-types", "dist")),
            (Source: Path.Combine(_options.Repository, "packages", "trpc", "dist"),
             Target: Path.Combine(packageDestination, "node_modules", "@repo", "trpc", "dist")),
        };

        foreach (var package in packages)
        {
            var entryPoint = Path.Combine(package.Target, "index.js");
            if (!File.Exists(entryPoint))
            {
                FileSystemUtil.CopyDirectory(package.Source, package.Target);
            }

            FileSystemUtil.RequireFile(entryPoint);
        }
    }

    private void CopyNodeRuntime()
    {
        var sourceDirectory = Path.GetDirectoryName(_node)!;
        var destination = Path.Combine(_options.Output, "runtime", "node");
        Directory.CreateDirectory(destination);

        foreach (var file in new[] { "node.exe", "npm.cmd", "npx.cmd", "LICENSE" })
        {
            var source = Path.Combine(sourceDirectory, file);
            if (File.Exists(source))
            {
                FileSystemUtil.CopyFile(source, Path.Combine(destination, file));
            }
        }

        var npmDirectory = Path.Combine(sourceDirectory, "node_modules", "npm");
        FileSystemUtil.CopyDirectory(
            npmDirectory,
            Path.Combine(destination, "node_modules", "npm"));
    }

    private async Task PublishHostAsync(CancellationToken cancellationToken)
    {
        var publishDirectory = Path.Combine(_options.ProjectRoot, ".host-publish");
        FileSystemUtil.RecreateDirectory(publishDirectory);
        await ProcessRunner.RunAsync(
            _dotnet,
            [
                "publish",
                _hostProject,
                "-c", "Release",
                "-r", "win-x64",
                "--self-contained", "true",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:PublishReadyToRun=false",
                "-o", publishDirectory,
            ],
            _options.ProjectRoot,
            cancellationToken: cancellationToken);
        FileSystemUtil.CopyFile(
            Path.Combine(publishDirectory, "MetaMCP.exe"),
            Path.Combine(_options.Output, "MetaMCP.exe"));
    }

    private void WriteConfiguration(string? preservedHostConfig)
    {
        var configDirectory = Path.Combine(_options.Output, "config");
        Directory.CreateDirectory(configDirectory);
        FileSystemUtil.CopyFile(
            Path.Combine(_options.Repository, ".env.local"),
            Path.Combine(configDirectory, ".env.local"));
        File.WriteAllText(
            Path.Combine(configDirectory, "host.json"),
            preservedHostConfig ?? File.ReadAllText(Path.Combine(_assetsDirectory, "host.json")));
    }

    private async Task WriteManifestAsync(CancellationToken cancellationToken)
    {
        var nodeVersion = await ProcessRunner.RunAsync(
            _node,
            ["--version"],
            _options.Repository,
            throwOnFailure: false,
            cancellationToken: cancellationToken);
        var pnpmVersion = await ProcessRunner.RunAsync(
            _pnpm,
            ["--version"],
            _options.Repository,
            throwOnFailure: false,
            cancellationToken: cancellationToken);

        string? gitCommit = null;
        try
        {
            var git = ProcessRunner.FindExecutable("git.exe", "git.cmd");
            var gitResult = await ProcessRunner.RunAsync(
                git,
                ["rev-parse", "--short", "HEAD"],
                _options.Repository,
                throwOnFailure: false,
                cancellationToken: cancellationToken);
            gitCommit = gitResult.Output.Trim();
        }
        catch
        {
        }

        var manifest = new
        {
            builtAt = DateTimeOffset.Now,
            sourceRepository = _options.Repository,
            gitCommit,
            nodeVersion = nodeVersion.Output.Trim(),
            pnpmVersion = pnpmVersion.Output.Trim(),
            frontendPort = 12008,
            backendPort = 12009,
        };
        File.WriteAllText(
            Path.Combine(_options.Output, "build-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ValidateRelease()
    {
        foreach (var file in new[]
        {
            Path.Combine(_options.Output, "MetaMCP.exe"),
            Path.Combine(_options.Output, "runtime", "node", "node.exe"),
            Path.Combine(_options.Output, "runtime", "node", "npx.cmd"),
            Path.Combine(_options.Output, "metamcp", "backend", "dist", "index.js"),
            Path.Combine(_options.Output, "metamcp", "backend", "scripts", "migrate.mjs"),
            Path.Combine(_options.Output, "metamcp", "frontend", "server.js"),
            Path.Combine(_options.Output, "metamcp", "frontend", "node_modules", "react-hook-form", "package.json"),
            Path.Combine(_options.Output, "config", ".env.local"),
            Path.Combine(_options.Output, "config", "host.json"),
        })
        {
            FileSystemUtil.RequireFile(file);
        }

        var brokenLinks = FileSystemUtil.GetBrokenReparsePoints(_options.Output);
        if (brokenLinks.Count > 0)
        {
            throw new InvalidOperationException(
                "The release contains broken links:\n" + string.Join('\n', brokenLinks.Take(20)));
        }

        var nonPortableLinks = FileSystemUtil.GetNonPortableReparsePoints(_options.Output);
        if (nonPortableLinks.Count > 0)
        {
            throw new InvalidOperationException(
                "The release contains absolute or unresolved links:\n"
                + string.Join('\n', nonPortableLinks.Take(20)));
        }

        var forbiddenScripts = Directory.EnumerateFiles(
                _options.Output,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".cmd" or ".bat" or ".ps1")
            .ToArray();
        if (forbiddenScripts.Length > 0)
        {
            throw new InvalidOperationException(
                "Custom launcher scripts leaked into the release:\n" + string.Join('\n', forbiddenScripts));
        }
    }

    private async Task SmokeTestAsync(CancellationToken cancellationToken)
    {
        const int backendPort = 12019;
        const int frontendPort = 12018;
        EnsurePortAvailable(backendPort);
        EnsurePortAvailable(frontendPort);

        var node = Path.Combine(_options.Output, "runtime", "node", "node.exe");
        var environment = LoadEnvironment(
            Path.Combine(_options.Output, "config", ".env.local"));
        environment["NODE_ENV"] = "production";
        environment["PATH"] = Path.GetDirectoryName(node)! + Path.PathSeparator
            + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        environment["METAMCP_NPX_CWD"] = Path.Combine(
            _options.Output,
            "data",
            "mcp-runners",
            "default");

        var backendEnvironment = new Dictionary<string, string>(environment)
        {
            ["BACKEND_HOST"] = "127.0.0.1",
            ["BACKEND_PORT"] = backendPort.ToString(),
        };
        var frontendEnvironment = new Dictionary<string, string>(environment)
        {
            ["HOSTNAME"] = "127.0.0.1",
            ["PORT"] = frontendPort.ToString(),
        };

        Process? backend = null;
        Process? frontend = null;
        try
        {
            backend = StartSmokeProcess(
                node,
                Path.Combine(_options.Output, "metamcp", "backend", "dist", "index.js"),
                Path.Combine(_options.Output, "metamcp", "backend"),
                backendEnvironment);
            await WaitForHttpAsync(
                backend,
                $"http://127.0.0.1:{backendPort}/health",
                TimeSpan.FromSeconds(60),
                cancellationToken);

            frontend = StartSmokeProcess(
                node,
                Path.Combine(_options.Output, "metamcp", "frontend", "server.js"),
                Path.Combine(_options.Output, "metamcp", "frontend"),
                frontendEnvironment);
            await WaitForHttpAsync(
                frontend,
                $"http://127.0.0.1:{frontendPort}/en",
                TimeSpan.FromSeconds(60),
                cancellationToken);
        }
        finally
        {
            StopSmokeProcess(frontend);
            StopSmokeProcess(backend);
        }
    }

    private static Process StartSmokeProcess(
        string node,
        string entry,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(node)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(entry);
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static async Task WaitForHttpAsync(
        Process process,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Smoke process exited with code {process.ExitCode}.\n{await errorTask}\n{await outputTask}");
            }

            try
            {
                using var response = await client.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(300, cancellationToken);
        }

        StopSmokeProcess(process);
        throw new TimeoutException(
            $"Packaged process did not become ready at {url}.\n{await errorTask}\n{await outputTask}");
    }

    private static void StopSmokeProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ValidateInputs()
    {
        foreach (var path in new[]
        {
            Path.Combine(_options.Repository, "package.json"),
            Path.Combine(_options.Repository, "pnpm-workspace.yaml"),
            Path.Combine(_options.Repository, ".env.local"),
            Path.Combine(_options.Repository, "apps", "backend", "package.json"),
            Path.Combine(_options.Repository, "apps", "frontend", "package.json"),
            _hostProject,
            Path.Combine(_assetsDirectory, "migrate.mjs"),
            Path.Combine(_assetsDirectory, "host.json"),
        })
        {
            FileSystemUtil.RequireFile(path);
        }
    }

    private void EnsureReleaseIsNotRunning()
    {
        if (!Directory.Exists(_options.Output))
        {
            return;
        }

        string? conflict = null;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (executable is not null &&
                    executable.StartsWith(
                        _options.Output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    conflict =
                        $"Release process PID {process.Id} is still running: {executable}. Stop MetaMCP before packaging.";
                    break;
                }
            }
            catch
            {
                // The process may exit or deny MainModule access while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (conflict is not null)
        {
            throw new InvalidOperationException(conflict);
        }
    }

    private static Dictionary<string, string> LoadEnvironment(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            values[key] = value;
        }
        return values;
    }

    private static void EnsurePortAvailable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port)
        {
            ExclusiveAddressUse = true,
        };
        try { listener.Start(); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"Smoke-test port {port} is occupied.", ex);
        }
        finally { listener.Stop(); }
    }

    private static string? ReadOptional(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static void Heading(string text)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine($"=== {text} ===");
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