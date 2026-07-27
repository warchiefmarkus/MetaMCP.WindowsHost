namespace MetaMCP.Packager;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var options = CommandLineOptions.Parse(args);
            Console.WriteLine("MetaMCP multi-platform production packager");
            Console.WriteLine($"Source:  {options.Repository}");
            Console.WriteLine($"Output:  {options.Output}");
            Console.WriteLine($"Targets: {string.Join(", ", options.Targets.Select(target => target.Id))}");
            var packager = new MultiPlatformPackager(options);
            await packager.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine("Packaging failed.");
            Console.Error.WriteLine(ex.Message);
            Console.ResetColor();
            return 1;
        }
    }
}