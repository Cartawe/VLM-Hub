using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Logging;

namespace VlmHub.Balancer.Server;

internal sealed class ServerMonitor
{
    private readonly ServerPool _pool;
    private readonly string _serversFile;
    private readonly ProcessingOptions _options;
    private readonly BalancerLogger _logger;
    private DateTimeOffset _nextConfigRefreshAt = DateTimeOffset.MinValue;

    public ServerMonitor(
        ServerPool pool,
        string serversFile,
        ProcessingOptions options,
        BalancerLogger logger)
    {
        _pool = pool;
        _serversFile = serversFile;
        _options = options;
        _logger = logger;
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        await RefreshConfigurationIfDueAsync(cancellationToken, force: true);
        var snapshot = _pool.Servers;
        await Task.WhenAll(snapshot.Select(server => RefreshServerAsync(server, cancellationToken)));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshConfigurationIfDueAsync(cancellationToken, force: false);

            var snapshot = _pool.Servers;
            await Task.WhenAll(snapshot.Select(server => RefreshServerAsync(server, cancellationToken)));

            await Task.Delay(_options.StatusPollInterval, cancellationToken);
        }
    }

    private async Task RefreshConfigurationIfDueAsync(
        CancellationToken cancellationToken,
        bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < _nextConfigRefreshAt)
        {
            return;
        }

        _nextConfigRefreshAt = now + _options.ServerConfigRefreshInterval;

        try
        {
            var definitions = await ServerConfiguration.LoadAsync(_serversFile, cancellationToken);
            _pool.Reconcile(definitions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Una edición incompleta o un JSON temporalmente inválido no debe
            // vaciar el pool: se conserva la última configuración válida.
            _logger.Warning(
                "server.config.reload_failed",
                "No se pudo recargar servidores.json; se conserva la última configuración válida.",
                data: new Dictionary<string, object?>
                {
                    ["path"] = _serversFile,
                    ["error"] = exception.Message
                });
        }
    }

    private async Task RefreshServerAsync(Runtime server, CancellationToken cancellationToken)
    {
        if (!server.IsCurrentlyConfigured())
        {
            return;
        }

        var context = new LogContext(Server: server.Name);
        var wasOnline = server.IsCurrentlyOnline();

        try
        {
            var status = await server.Client.GetStatusAsync(
                _options.StatusRequestTimeout,
                cancellationToken,
                context);

            var update = server.UpdateStatus(status, _options.StuckStateTimeout);

            if (update.CameOnline)
            {
                _logger.Info(
                    update.FirstOnline ? "server.online.initial" : "server.online",
                    update.FirstOnline
                        ? $"Servidor disponible: {server.Name}."
                        : $"Servidor reincorporado automáticamente: {server.Name}.",
                    context,
                    StatusData(status, server));
            }
            else if (server.IsStuck && update.Changed)
            {
                _logger.Warning(
                    "server.stuck",
                    $"{server.Name} permanece demasiado tiempo en {status.State}; queda fuera del reparto hasta recuperarse.",
                    context,
                    StatusData(status, server));
            }
            else if (update.Changed && IsImportantState(status.State))
            {
                _logger.Info(
                    "server.state",
                    $"{server.Name}: {status.State}.",
                    context,
                    StatusData(status, server));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var transitioned = server.MarkOffline(exception.Message, _options.ServerCooldown);
            if (transitioned)
            {
                _logger.Warning(
                    "server.offline",
                    $"Servidor fuera de línea: {server.Name}.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["error"] = exception.Message
                    });
            }

            return;
        }

        // Al volver de una caída, UpdateStatus fuerza refresco inmediato de
        // modelos. En funcionamiento normal se actualiza según el intervalo.
        if (!server.ShouldRefreshModels())
        {
            return;
        }

        try
        {
            var models = await server.Client.GetModelsAsync(
                _options.StatusRequestTimeout,
                cancellationToken,
                context);

            var changed = server.UpdateModels(models, _options.ModelListRefreshInterval);
            if (changed || !wasOnline)
            {
                _logger.Info(
                    "server.models.loaded",
                    $"{server.Name}: {models.Count} modelo(s) disponibles.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["models"] = models.OrderBy(model => model).ToArray(),
                        ["model_count"] = models.Count
                    });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // /api/status sí respondió: el nodo no se marca offline por un
            // fallo aislado de /api/model/list.
            server.DeferModelRefresh(_options.ModelFailureCooldown);
            _logger.Warning(
                "server.models.refresh_failed",
                $"No se pudo actualizar el catálogo de modelos de {server.Name}.",
                context,
                new Dictionary<string, object?> { ["error"] = exception.Message });
        }
    }

    private static bool IsImportantState(string state) =>
        state.Equals("Disponible", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("SinModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ErrorModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ErrorDaemon", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, object?> StatusData(
        VlmHub.Balancer.Models.Status status,
        Runtime server) =>
        new Dictionary<string, object?>
        {
            ["state"] = status.State,
            ["active_model"] = status.ActiveModel,
            ["last_error"] = status.LastError,
            ["last_state_change"] = status.LastStateChange,
            ["stuck"] = server.IsStuck
        };
}
