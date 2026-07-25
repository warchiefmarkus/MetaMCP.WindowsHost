using Microsoft.Extensions.Hosting;

namespace MetaMCP.Host;

internal sealed class ServiceWorker(
    RuntimeController runtime,
    PipeServer pipeServer,
    HostSettings settings) : BackgroundService
{
    private readonly RuntimeController _runtime = runtime;
    private readonly PipeServer _pipeServer = pipeServer;
    private readonly HostSettings _settings = settings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeTask = _pipeServer.RunAsync(stoppingToken);
        if (_settings.AutoStartRuntime)
        {
            try
            {
                await _runtime.StartAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                HostLog.Error("Service auto-start of MetaMCP failed.", ex);
            }
        }

        await pipeTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _runtime.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            HostLog.Error("Service runtime shutdown failed.", ex);
        }

        await base.StopAsync(cancellationToken);
    }
}