using System.Text.Json;

namespace MetaMCP.Host;

internal static class PipeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

internal static class PipeCommands
{
    public const string Status = "status";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";
}

internal sealed record PipeRequest(string Command);

internal sealed record PipeResponse(
    bool Success,
    RuntimeStatus? Status,
    string? Error)
{
    public static PipeResponse Ok(RuntimeStatus status) => new(true, status, null);
    public static PipeResponse Fail(string error, RuntimeStatus? status = null) =>
        new(false, status, error);
}