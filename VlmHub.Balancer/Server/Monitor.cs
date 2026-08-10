using VlmHub.Balancer.Configuration;

namespace VlmHub.Balancer.Server;

internal sealed class ServerMonitor
{
    private readonly IReadOnlyList<Runtime> _servers;
    private readonly ProcessingOptions _options;

    public ServerMonitor(IReadOnlyList<Runtime> servers, ProcessingOptions options)
    {
        _servers = servers;
        _options = options;
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_servers.Select(server => RefreshServerAsync(server, cancellationToken)));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshAllAsync(cancellationToken);
            await Task.Delay(_options.StatusPollInterval, cancellationToken);
        }
    }

    private async Task RefreshServerAsync(
        Runtime server,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await server.Client.GetStatusAsync(
                _options.StatusRequestTimeout,
                cancellationToken);

            server.UpdateStatus(status, _options.StuckStateTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            server.MarkOffline(exception.Message, _options.ServerCooldown);
            return;
        }

        if (DateTimeOffset.UtcNow - server.GetModelsRefreshedAt() < _options.ModelListRefreshInterval)
        {
            return;
        }

        try
        {
            var models = await server.Client.GetModelsAsync(
                _options.StatusRequestTimeout,
                cancellationToken);

            server.UpdateModels(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Un fallo al refrescar el catálogo de modelos no convierte un
            // servidor que respondió /api/status en un servidor caído.
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
        }
    }
}
