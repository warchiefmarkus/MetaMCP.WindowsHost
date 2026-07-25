using System.Diagnostics;
using System.Text;

namespace MetaMCP.Packager;

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

internal static class ProcessRunner
{
    public static string FindExecutable(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in names)
        {
            if (Path.IsPathRooted(name) && File.Exists(name))
            {
                return Path.GetFullPath(name);
            }

            foreach (var directory in path.Split(Path.PathSeparator))
            {
                var trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(trimmed, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"Could not locate any of these commands in PATH: {string.Join(", ", names)}");
    }

    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        bool throwOnFailure = true,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"> {Path.GetFileName(executable)} {string.Join(' ', argumentList.Select(QuoteDisplay))}");
        Console.ResetColor();

        var startInfo = CreateStartInfo(executable, argumentList, workingDirectory);
        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            Console.WriteLine(eventArgs.Data);
            output.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            Console.Error.WriteLine(eventArgs.Data);
            error.AppendLine(eventArgs.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {executable}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        var result = new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed with exit code {result.ExitCode}: {executable}\n{Tail(result.Error, result.Output)}");
        }

        return result;
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var command = string.Join(' ', new[] { QuoteCmd(executable) }
                .Concat(arguments.Select(QuoteCmd)));
            var shell = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            shell.ArgumentList.Add("/d");
            shell.ArgumentList.Add("/s");
            shell.ArgumentList.Add("/c");
            shell.ArgumentList.Add(command);
            return shell;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string QuoteCmd(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('&') || value.Contains('(')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    private static string QuoteDisplay(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static string Tail(params string[] values)
    {
        var lines = values
            .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .TakeLast(80);
        return string.Join(Environment.NewLine, lines);
    }
}