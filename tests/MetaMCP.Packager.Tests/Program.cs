using MetaMCP.Packager;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SKIP: Windows cmd.exe regression tests.");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "MetaMCP runner tests " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var batchPath = Path.Combine(root, "fake npm.cmd");
    File.WriteAllText(batchPath,
        "@echo off\r\n" +
        "echo ARG1=[%~1]\r\n" +
        "echo ARG2=[%~2]\r\n" +
        "echo ENV=[%METAMCP_RUNNER_TEST%]\r\n" +
        "exit /b 0\r\n");

    var spacedArg = Path.Combine(root, "some directory with spaces");
    var result = await ProcessRunner.RunAsync(
        batchPath,
        ["install", spacedArg],
        root,
        new Dictionary<string, string> { ["METAMCP_RUNNER_TEST"] = "set" });

    Require(result.ExitCode == 0, "Batch command returned nonzero.");
    Require(result.Output.Contains("ARG1=[install]", StringComparison.Ordinal),
        "First argument was corrupted.");
    Require(result.Output.Contains("ARG2=[" + spacedArg + "]", StringComparison.Ordinal),
        "Argument containing spaces was corrupted.");
    Require(result.Output.Contains("ENV=[set]", StringComparison.Ordinal),
        "Child process environment was not propagated.");
    Console.WriteLine("PASS: .cmd in a spaced path, spaced arguments and environment.");

    var batPath = Path.Combine(root, "failure script.bat");
    File.WriteAllText(batPath, "@echo off\r\necho expected failure\r\nexit /b 17\r\n");

    var failure = await ProcessRunner.RunAsync(
        batPath, [], root, throwOnFailure: false);
    Require(failure.ExitCode == 17, "Batch exit code was not propagated.");
    Console.WriteLine("PASS: .bat exit code propagation.");

    Console.WriteLine("ProcessRunner regression tests: 2 passed; 0 failed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
