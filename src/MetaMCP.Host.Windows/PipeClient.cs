using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaMCP.Host;

internal static class PipeClient
{
    public static async Task<PipeResponse> SendAsync(
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        string? mappingId = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

        await using var pipe = new NamedPipeClientStream(
            ".",
            HostConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(timeoutSource.Token);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        var request = new PipeRequest(command, mappingId);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeJson.Options));
        var responseLine = await reader.ReadLineAsync(timeoutSource.Token);
        return responseLine is null
            ? PipeResponse.Fail("The service closed the control pipe without a response.")
            : JsonSerializer.Deserialize<PipeResponse>(responseLine, PipeJson.Options)
                ?? PipeResponse.Fail("The service returned an invalid response.");
    }
}