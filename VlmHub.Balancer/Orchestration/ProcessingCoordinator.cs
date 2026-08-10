using System.Text.Json;
using VlmHub.Balancer.Api;
using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Models;
using VlmHub.Balancer.Server;

namespace VlmHub.Balancer.Orchestration;

/// <summary>
/// Balancea las unidades de procesamiento entre VLM Servers y aplica las
/// políticas de reintento, recuperación y reconstrucción documental.
/// </summary>
public sealed class ProcessingCoordinator
{
    private readonly ProcessingOptions _options;

    public ProcessingCoordinator(ProcessingOptions? options = null)
    {
        _options = options ?? new ProcessingOptions();
    }

    public Task<BatchResult> ProcessAsync(
        string document,
        IReadOnlyList<string> modelPriority,
        int maxAttempts,
        string serversFile,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        return ProcessAsync(
            new ProcessingRequest
            {
                Documents = [document],
                ModelPriority = modelPriority,
                MaxAttempts = maxAttempts,
                ServersFile = serversFile,
                OutputDirectory = outputDirectory
            },
            cancellationToken);
    }

    public Task<BatchResult> ProcessAsync(
        IReadOnlyList<string> documents,
        IReadOnlyList<string> modelPriority,
        int maxAttempts,
        string serversFile,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        return ProcessAsync(
            new ProcessingRequest
            {
                Documents = documents,
                ModelPriority = modelPriority,
                MaxAttempts = maxAttempts,
                ServersFile = serversFile,
                OutputDirectory = outputDirectory
            },
            cancellationToken);
    }

    public async Task<BatchResult> ProcessAsync(
        ProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        var servers = await LoadServersAsync(request.ServersFile, cancellationToken);
        using var serverPool = CreateServerPool(servers);
        var monitor = new ServerMonitor(serverPool.Servers, _options);
        var preprocessor = new DocumentPreprocessor(_options);
        var resultWriter = new ResultWriter();

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "VlmHub",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workingDirectory);

        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? monitorTask = null;

        try
        {
            var documents = await preprocessor.PrepareAsync(
                request.Documents,
                workingDirectory,
                cancellationToken);

            await monitor.RefreshAllAsync(cancellationToken);
            monitorTask = monitor.RunAsync(monitorCts.Token);

            await DispatchAsync(documents, request, serverPool, cancellationToken);

            var processed = new List<ProcessedDocument>(documents.Count);

            foreach (var document in documents)
            {
                string? outputPath = null;
                string? documentError = document.PreparationError;

                try
                {
                    outputPath = await resultWriter.WriteAsync(
                        document,
                        request.OutputDirectory,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    documentError = string.IsNullOrWhiteSpace(documentError)
                        ? exception.Message
                        : $"{documentError} | No se pudo escribir resultado: {exception.Message}";
                }

                var completed =
                    document.PreparationError is null &&
                    document.Units.Count > 0 &&
                    document.Units.All(unit => unit.IsCompleted);

                if (!completed && string.IsNullOrWhiteSpace(documentError))
                {
                    documentError = document.Units
                        .Where(unit => !unit.IsCompleted)
                        .Select(unit => unit.LastError ?? unit.LastFinishReason)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                }

                processed.Add(new ProcessedDocument(
                    document.SourcePath,
                    outputPath,
                    completed,
                    documentError,
                    document.Units.Select(unit => new ProcessingUnitSummary(
                        unit.PageNumber,
                        unit.IsCompleted,
                        unit.VlmAttempts,
                        unit.InfrastructureFailures,
                        unit.LastFinishReason,
                        unit.LastModel,
                        unit.LastServer,
                        unit.LastError)).ToArray()));
            }

            return new BatchResult(processed);
        }
        finally
        {
            monitorCts.Cancel();

            if (monitorTask is not null)
            {
                try
                {
                    await monitorTask;
                }
                catch (OperationCanceledException) when (monitorCts.IsCancellationRequested)
                {
                    // Fin normal del monitor al terminar el lote.
                }
            }

            TryDeleteWorkingDirectory(workingDirectory);
        }
    }

    private async Task DispatchAsync(
        IReadOnlyList<PreparedDocument> documents,
        ProcessingRequest request,
        ServerPool serverPool,
        CancellationToken cancellationToken)
    {
        var pending = new List<ProcessingUnit>(documents.SelectMany(document => document.Units));

        if (pending.Count == 0)
        {
            return;
        }

        if (serverPool.Servers.Count == 0)
        {
            foreach (var unit in pending)
            {
                unit.FailPermanently("No existen VLM Servers configurados para procesar el lote.");
            }

            return;
        }

        if (request.ModelPriority.Count == 0)
        {
            foreach (var unit in pending)
            {
                unit.FailPermanently("No existe una prioridad de modelos para procesar el lote.");
            }

            return;
        }

        var active = new Dictionary<Guid, ActiveDispatch>();
        DateTimeOffset? noServerSince = null;

        while (pending.Any(unit => !unit.IsCompleted && !unit.IsTerminalFailure) || active.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dispatchedAny = false;

            foreach (var unit in pending
                         .Where(unit => !unit.IsCompleted &&
                                        !unit.IsTerminalFailure &&
                                        unit.NextEligibleAt <= DateTimeOffset.UtcNow)
                         .ToArray())
            {
                var model = ModelPriorityPolicy.ResolveModel(unit, request.ModelPriority, serverPool);
                if (model is null)
                {
                    continue;
                }

                var server = serverPool.SelectBest(model);
                if (server is null || !server.Slot.Wait(0))
                {
                    continue;
                }

                pending.Remove(unit);
                server.MarkAssigned();
                unit.MarkServer(server.Name);
                dispatchedAny = true;

                active[unit.Id] = new ActiveDispatch(
                    unit,
                    ProcessOnServerAsync(
                        unit,
                        model,
                        request,
                        server,
                        cancellationToken));
            }

            foreach (var completedId in active
                         .Where(pair => pair.Value.Task.IsCompleted)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                var dispatch = active[completedId];

                try
                {
                    var disposition = await dispatch.Task;
                    if (disposition == DispatchDisposition.Requeue &&
                        !dispatch.Unit.IsCompleted &&
                        !dispatch.Unit.IsTerminalFailure)
                    {
                        pending.Add(dispatch.Unit);
                    }
                }
                finally
                {
                    active.Remove(completedId);
                }
            }

            var unresolved = pending.Any(unit => !unit.IsCompleted && !unit.IsTerminalFailure);

            // El timeout de indisponibilidad solo avanza cuando realmente no
            // existe trabajo en vuelo ni fue posible despachar nada. Una tarea
            // larga no provoca por sí sola que el lote sea abortado.
            if (!unresolved || active.Count > 0 || dispatchedAny)
            {
                noServerSince = null;
            }
            else
            {
                noServerSince ??= DateTimeOffset.UtcNow;

                if (DateTimeOffset.UtcNow - noServerSince >= _options.NoServerAvailableTimeout)
                {
                    foreach (var unit in pending.Where(unit => !unit.IsCompleted && !unit.IsTerminalFailure))
                    {
                        unit.FailPermanently(
                            serverPool.AnyOnline
                                ? "No hay un servidor disponible con los modelos solicitados."
                                : "No hay VLM Servers accesibles para continuar el procesamiento.");
                    }

                    break;
                }
            }

            if (active.Count > 0)
            {
                await Task.WhenAny(
                    active.Values
                        .Select(dispatch => (Task)dispatch.Task)
                        .Append(Task.Delay(_options.SchedulerDelay, cancellationToken)));
            }
            else
            {
                await Task.Delay(_options.SchedulerDelay, cancellationToken);
            }
        }
    }

    private async Task<DispatchDisposition> ProcessOnServerAsync(
        ProcessingUnit unit,
        string model,
        ProcessingRequest request,
        Runtime server,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!server.HasActiveModel(model))
            {
                await server.Client.EnsureModelAsync(
                    model,
                    _options.ProcessingRequestTimeout,
                    cancellationToken);

                server.MarkModelActive(model);
            }

            var response = await server.Client.ProcessImageAsync(
                unit.ImagePath,
                _options.ProcessingRequestTimeout,
                cancellationToken);

            switch (response.FinishReason)
            {
                case "stop":
                    unit.Complete(response);
                    return DispatchDisposition.Done;

                case "error":
                case "length":
                case "lenght":
                    unit.RegisterVlmFailure(
                        response,
                        request.MaxAttempts,
                        request.ModelPriority.Count);

                    unit.NextEligibleAt = DateTimeOffset.UtcNow;
                    return unit.IsTerminalFailure
                        ? DispatchDisposition.Done
                        : DispatchDisposition.Requeue;

                default:
                    unit.FailPermanently(
                        $"finish_reason no reconocido: '{response.FinishReason}'.");
                    return DispatchDisposition.Done;
            }
        }
        catch (VlmModelUnavailableException exception)
        {
            // El servidor existe, pero falló preparando este modelo. Se bloquea
            // temporalmente esa combinación servidor/modelo para que el mismo
            // trabajo pueda migrar a otro nodo o a la siguiente prioridad.
            server.BlockModel(model, _options.ModelFailureCooldown);
            unit.RegisterTransientFailure(
                exception.Message,
                _options.SchedulerDelay,
                _options.MaxInfrastructureFailuresPerUnit);
            return unit.IsTerminalFailure
                ? DispatchDisposition.Done
                : DispatchDisposition.Requeue;
        }
        catch (VlmServerBusyException exception)
        {
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            unit.RegisterTransientFailure(
                exception.Message,
                _options.ServerCooldown,
                _options.MaxInfrastructureFailuresPerUnit);
            return unit.IsTerminalFailure
                ? DispatchDisposition.Done
                : DispatchDisposition.Requeue;
        }
        catch (VlmServerTransientException exception)
        {
            // Una caída, timeout, HTTP 408/500/503 o error de transporte no
            // consume MaxAttempts porque no existe finish_reason VLM válido.
            server.MarkOffline(exception.Message, _options.ServerCooldown);
            unit.RegisterTransientFailure(
                exception.Message,
                _options.ServerCooldown,
                _options.MaxInfrastructureFailuresPerUnit);
            return unit.IsTerminalFailure
                ? DispatchDisposition.Done
                : DispatchDisposition.Requeue;
        }
        catch (IOException exception)
        {
            // El problema está en el archivo local, no en el VLM Server.
            unit.FailPermanently(exception.Message);
            return DispatchDisposition.Done;
        }
        catch (UnauthorizedAccessException exception)
        {
            unit.FailPermanently(exception.Message);
            return DispatchDisposition.Done;
        }
        catch (VlmRequestException exception)
        {
            unit.FailPermanently(exception.Message);
            return DispatchDisposition.Done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fallos inesperados de una unidad no derriban el lote completo.
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            unit.RegisterTransientFailure(
                exception.Message,
                _options.ServerCooldown,
                _options.MaxInfrastructureFailuresPerUnit);
            return unit.IsTerminalFailure
                ? DispatchDisposition.Done
                : DispatchDisposition.Requeue;
        }
        finally
        {
            server.Slot.Release();
        }
    }

    private enum DispatchDisposition
    {
        Done,
        Requeue
    }

    private sealed record ActiveDispatch(
        ProcessingUnit Unit,
        Task<DispatchDisposition> Task);

    private static ServerPool CreateServerPool(IReadOnlyList<ServerDefinition> definitions)
    {
        var runtimes = definitions
            .Select(definition => new Runtime(new VlmServerClient(definition)))
            .ToArray();

        return new ServerPool(runtimes);
    }

    private static async Task<IReadOnlyList<ServerDefinition>> LoadServersAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<ServerDefinition[]>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                   cancellationToken)
               ?? Array.Empty<ServerDefinition>();
    }

    private static void TryDeleteWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Los temporales no deben convertir un lote ya procesado en error.
        }
    }
}
