using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MetaMCP.Host;

internal sealed class PipeServer(RuntimeController runtime)
{
    private readonly RuntimeController _runtime = runtime;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServer();
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                HostLog.Error("Named pipe server request failed.", ex);
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        PipeResponse response;
        try
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            var request = line is null
                ? null
                : JsonSerializer.Deserialize<PipeRequest>(line, PipeJson.Options);
            response = request is null
                ? PipeResponse.Fail("Invalid pipe request.")
                : await ExecuteAsync(request.Command, cancellationToken);
        }
        catch (Exception ex)
        {
            response = PipeResponse.Fail(ex.Message, _runtime.CurrentStatus);
        }

        await writer.WriteLineAsync(
            JsonSerializer.Serialize(response, PipeJson.Options));
    }

    private async Task<PipeResponse> ExecuteAsync(
        string command,
        CancellationToken cancellationToken)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case PipeCommands.Status:
                return PipeResponse.Ok(await _runtime.RefreshStatusAsync(cancellationToken));
            case PipeCommands.Start:
                await _runtime.StartAsync(cancellationToken);
                return PipeResponse.Ok(await _runtime.RefreshStatusAsync(cancellationToken));
            case PipeCommands.Stop:
                await _runtime.StopAsync(cancellationToken);
                return PipeResponse.Ok(await _runtime.RefreshStatusAsync(cancellationToken));
            case PipeCommands.Restart:
                await _runtime.RestartAsync(cancellationToken);
                return PipeResponse.Ok(await _runtime.RefreshStatusAsync(cancellationToken));
            default:
                return PipeResponse.Fail($"Unknown command: {command}", _runtime.CurrentStatus);
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            HostConstants.PipeName,
            PipeDirection.InOut,
            5,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }
}