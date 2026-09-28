using System.Net.Http.Json;
using System.Text.Json;

namespace MetaMCP.Host;

internal static class RuntimeControlClient
{
    public static async Task<int> RunAsync(
        string baseDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException(
                "Runtime command is required: status | swap [A|B].");
        }

        var settings = HostSettings.Load(baseDirectory);
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        http.DefaultRequestHeaders.Add(
            "X-MetaMCP-Host-Control-Token",
            settings.HostControlToken);

        var controlBase =
            $"http://127.0.0.1:{settings.BackendPort}/__host/runtime";
        HttpResponseMessage response;

        switch (arguments[0].Trim().ToLowerInvariant())
        {
            case "status":
                response = await http.GetAsync(
                    $"{controlBase}/status",
                    cancellationToken);
                break;

            case "swap":
                var slot = ParseSlot(arguments.Skip(1).ToArray());
                response = await http.PostAsJsonAsync(
                    $"{controlBase}/swap",
                    new { slot },
                    cancellationToken);
                break;

            default:
                throw new ArgumentException(
                    $"Unknown runtime command '{arguments[0]}'. Use status or swap.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Runtime control returned HTTP {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            body = "{}";
        }

        using var document = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(
            document.RootElement,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string? ParseSlot(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        string value;
        if (arguments.Count == 1)
        {
            value = arguments[0];
        }
        else if (arguments.Count == 2 &&
            arguments[0].Equals("--slot", StringComparison.OrdinalIgnoreCase))
        {
            value = arguments[1];
        }
        else
        {
            throw new ArgumentException(
                "Usage: MetaMCP.exe runtime swap [A|B] or runtime swap --slot A|B.");
        }

        var slot = value.Trim().ToUpperInvariant();
        return slot is "A" or "B"
            ? slot
            : throw new ArgumentOutOfRangeException(
                nameof(arguments),
                "Runtime slot must be A or B.");
    }
}
