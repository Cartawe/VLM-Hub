using System.Collections.Concurrent;
using VlmHub.Balancer.Api;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Server;

internal sealed class ServerPool : IDisposable
{
    private readonly ConcurrentDictionary<string, Runtime> _servers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly BalancerLogger _logger;

    public ServerPool(
        IReadOnlyList<ServerDefinition> definitions,
        BalancerLogger logger)
    {
        _logger = logger;
        Reconcile(definitions, logChanges: false);
    }

    public IReadOnlyList<Runtime> Servers =>
        _servers.Values
            .Where(server => server.IsCurrentlyConfigured())
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<Runtime> AllServers => _servers.Values.ToArray();

    /// <summary>
    /// Aplica la última versión válida de servidores.json sin detener el lote.
    /// Nuevos nodos se incorporan; nodos retirados dejan de recibir trabajo.
    /// Una operación ya activa en un nodo retirado puede finalizar normalmente.
    /// </summary>
    public void Reconcile(IReadOnlyList<ServerDefinition> definitions, bool logChanges = true)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            string key;
            try
            {
                key = Runtime.BuildConfigurationKey(definition);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "server.config.invalid",
                    $"No se pudo cargar la definición de servidor '{definition.Name}'.",
                    exception,
                    new LogContext(Server: definition.Name));
                continue;
            }

            desired.Add(key);

            if (_servers.TryGetValue(key, out var existing))
            {
                var wasConfigured = existing.IsCurrentlyConfigured();
                existing.SetConfigured(true);

                if (logChanges && !wasConfigured)
                {
                    _logger.Info(
                        "server.config.added",
                        $"Servidor habilitado por configuración: {existing.Name}.",
                        new LogContext(Server: existing.Name),
                        new Dictionary<string, object?>
                        {
                            ["url"] = existing.Client.Definition.BuildBaseUri().ToString()
                        });
                }

                continue;
            }

            try
            {
                var runtime = new Runtime(new VlmServerClient(definition, _logger));
                if (_servers.TryAdd(key, runtime))
                {
                    if (logChanges)
                    {
                        _logger.Info(
                            "server.config.added",
                            $"Nuevo servidor detectado en configuración: {definition.Name}.",
                            new LogContext(Server: definition.Name),
                            new Dictionary<string, object?>
                            {
                                ["url"] = definition.BuildBaseUri().ToString()
                            });
                    }
                }
                else
                {
                    runtime.Dispose();
                }
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "server.config.invalid",
                    $"No se pudo inicializar el servidor '{definition.Name}'.",
                    exception,
                    new LogContext(Server: definition.Name));
            }
        }

        foreach (var pair in _servers)
        {
            if (desired.Contains(pair.Key))
            {
                continue;
            }

            var server = pair.Value;
            if (!server.IsCurrentlyConfigured())
            {
                continue;
            }

            server.SetConfigured(false);

            if (logChanges)
            {
                _logger.Warning(
                    "server.config.removed",
                    $"Servidor retirado de la configuración: {server.Name}. No recibirá nuevos trabajos.",
                    new LogContext(Server: server.Name));
            }
        }
    }

    public IReadOnlyList<Runtime> GetFreeServersSnapshot() =>
        Servers
            .Where(server => server.Slot.CurrentCount > 0)
            .OrderBy(server => server.GetLastAssignedAt())
            .ToArray();

    public bool AnyOnline => Servers.Any(server => server.IsCurrentlyOnline());

    public bool AnyTemporarilyBusyForAny(IReadOnlyList<string> models) =>
        Servers.Any(server => server.IsTemporarilyBusyForAny(models));

    public bool AnyPotentialCandidate(IReadOnlyList<string> models) =>
        Servers.Any(server => server.CanEventuallyServeAny(models));

    public void Dispose()
    {
        foreach (var server in _servers.Values)
        {
            server.Dispose();
        }

        _servers.Clear();
    }
}
