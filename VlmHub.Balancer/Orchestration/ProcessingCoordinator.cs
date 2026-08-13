using System.Diagnostics;
using VlmHub.Balancer.Api;
using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;
using VlmHub.Balancer.Server;

namespace VlmHub.Balancer.Orchestration;

/// <summary>
/// Balancea imágenes/páginas entre todos los VLM Servers configurados.
///
/// El lote se procesa por ventanas acotadas para que miles de documentos no
/// permanezcan preparados en memoria simultáneamente. Cada servidor mantiene un
/// único trabajo activo, mientras servidores distintos trabajan en paralelo.
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
        CancellationToken cancellationToken = default) =>
        ProcessAsync(
            new ProcessingRequest
            {
                Documents = [document],
                ModelPriority = modelPriority,
                MaxAttempts = maxAttempts,
                ServersFile = serversFile,
                OutputDirectory = outputDirectory
            },
            cancellationToken);

    public Task<BatchResult> ProcessAsync(
        IReadOnlyList<string> documents,
        IReadOnlyList<string> modelPriority,
        int maxAttempts,
        string serversFile,
        string outputDirectory,
        CancellationToken cancellationToken = default) =>
        ProcessAsync(
            new ProcessingRequest
            {
                Documents = documents,
                ModelPriority = modelPriority,
                MaxAttempts = maxAttempts,
                ServersFile = serversFile,
                OutputDirectory = outputDirectory
            },
            cancellationToken);

    public async Task<BatchResult> ProcessAsync(
        ProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        var batchId = Guid.NewGuid();
        var startedAt = DateTimeOffset.Now;
        using var logger = BalancerLogger.Create(
            batchId,
            _options.LogDirectory,
            request.ProcessingId,
            _options.SharedLogger);

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "VlmHub",
            batchId.ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var keepWorkingDirectory = false;
        ServerPool? serverPool = null;
        CancellationTokenSource? monitorCts = null;
        Task? monitorTask = null;

        logger.Info(
            "batch.started",
            "Lote de procesamiento iniciado.",
            data: new Dictionary<string, object?>
            {
                ["documents"] = request.Documents.Count,
                ["model_priority"] = request.ModelPriority.ToArray(),
                ["max_vlm_errors"] = request.MaxAttempts,
                ["max_global_failures"] = _options.MaxGlobalFailuresPerUnit,
                ["max_documents_in_memory"] = _options.MaxDocumentsInMemory,
                ["servers_file"] = request.ServersFile,
                ["output_directory"] = request.OutputDirectory,
                ["working_directory"] = workingDirectory
            });

        try
        {
            var initialDefinitions = await ServerConfiguration.LoadAsync(
                request.ServersFile,
                cancellationToken);

            serverPool = new ServerPool(initialDefinitions, logger);
            var monitor = new ServerMonitor(
                serverPool,
                request.ServersFile,
                _options,
                logger);
            var preprocessor = new DocumentPreprocessor(_options, logger);
            var resultWriter = new ResultWriter(logger);

            var windowSize = ResolveWindowSize();
            var nextOffset = 0;
            var windowNumber = 0;

            // La primera preparación se solapa con la fotografía inicial de la
            // infraestructura para reducir el tiempo hasta el primer despacho.
            Task<IReadOnlyList<PreparedDocument>>? currentPrepareTask = null;
            if (request.Documents.Count > 0)
            {
                var firstPaths = CopyWindow(request.Documents, nextOffset, windowSize);
                nextOffset += firstPaths.Count;
                currentPrepareTask = preprocessor.PrepareAsync(
                    firstPaths,
                    workingDirectory,
                    cancellationToken);
            }

            await monitor.RefreshAllAsync(cancellationToken);
            monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            monitorTask = monitor.RunAsync(monitorCts.Token);

            var processed = new List<ProcessedDocument>(request.Documents.Count);
            var failedDocuments = 0;

            while (currentPrepareTask is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                windowNumber++;

                var documents = await currentPrepareTask;

                // Prefetch de la siguiente ventana mientras la actual usa los VLM.
                Task<IReadOnlyList<PreparedDocument>>? nextPrepareTask = null;
                if (nextOffset < request.Documents.Count)
                {
                    var nextPaths = CopyWindow(request.Documents, nextOffset, windowSize);
                    nextOffset += nextPaths.Count;
                    nextPrepareTask = preprocessor.PrepareAsync(
                        nextPaths,
                        workingDirectory,
                        cancellationToken);
                }

                logger.Info(
                    "batch.window.started",
                    "Ventana de documentos lista para procesamiento.",
                    data: new Dictionary<string, object?>
                    {
                        ["window"] = windowNumber,
                        ["documents"] = documents.Count,
                        ["range"] = $"{processed.Count + 1}-{processed.Count + documents.Count}"
                    });
                LogMemorySnapshot(logger, windowNumber, "ventana cargada");

                await InitializeResultsAsync(
                    documents,
                    request.OutputDirectory,
                    resultWriter,
                    logger,
                    cancellationToken);

                await DispatchAsync(
                    documents,
                    request,
                    serverPool,
                    resultWriter,
                    logger,
                    cancellationToken);

                foreach (var document in documents)
                {
                    var summary = BuildProcessedDocument(document);
                    processed.Add(summary);

                    if (!summary.Completed)
                    {
                        failedDocuments++;
                    }

                    var keepDocumentTemporary =
                        !summary.Completed && _options.KeepTemporaryFilesOnFailure;

                    if (!keepDocumentTemporary)
                    {
                        TryDeleteDocumentTemporaryDirectory(document, logger);
                    }

                    // Rompe referencias a todas las unidades apenas el resumen
                    // fue materializado; evita mantener rutas/estado de páginas
                    // hasta que termine la iteración de la ventana.
                    document.Units = Array.Empty<ProcessingUnit>();
                    document.Dispose();
                }

                LogMemorySnapshot(logger, windowNumber, "ventana finalizada");

                // Al pasar a la siguiente iteración, PreparedDocument y sus
                // unidades dejan de estar referenciados salvo por el task de
                // prefetch. El contenido reconocido nunca se almacena en ellos.
                currentPrepareTask = nextPrepareTask;
            }

            keepWorkingDirectory = failedDocuments > 0 && _options.KeepTemporaryFilesOnFailure;

            logger.Info(
                "batch.completed",
                "Lote finalizado.",
                data: new Dictionary<string, object?>
                {
                    ["completed_documents"] = processed.Count - failedDocuments,
                    ["failed_documents"] = failedDocuments,
                    ["duration_ms"] = (DateTimeOffset.Now - startedAt).TotalMilliseconds,
                    ["temporary_files_retained"] = keepWorkingDirectory
                });

            return new BatchResult(processed)
            {
                BatchId = batchId,
                LogPath = logger.LogPath,
                WorkingDirectory = keepWorkingDirectory ? workingDirectory : null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            keepWorkingDirectory = _options.KeepTemporaryFilesOnFailure;
            logger.Warning("batch.cancelled", "El lote fue cancelado por el llamador.");
            throw;
        }
        catch (Exception exception)
        {
            keepWorkingDirectory = _options.KeepTemporaryFilesOnFailure;
            logger.Error(
                "batch.fatal_error",
                "El lote no pudo continuar por un error global.",
                exception,
                data: new Dictionary<string, object?>
                {
                    ["working_directory"] = workingDirectory,
                    ["duration_ms"] = (DateTimeOffset.Now - startedAt).TotalMilliseconds
                });

            return new BatchResult(Array.Empty<ProcessedDocument>())
            {
                BatchId = batchId,
                LogPath = logger.LogPath,
                WorkingDirectory = keepWorkingDirectory ? workingDirectory : null,
                FatalError = exception.Message
            };
        }
        finally
        {
            if (monitorCts is not null)
            {
                monitorCts.Cancel();
            }

            if (monitorTask is not null)
            {
                try
                {
                    await monitorTask;
                }
                catch (OperationCanceledException) when (monitorCts?.IsCancellationRequested == true)
                {
                    // Cierre normal.
                }
                catch (Exception exception)
                {
                    logger.Error("monitor.stop_failed", "El monitor terminó con un error.", exception);
                }
            }

            monitorCts?.Dispose();
            serverPool?.Dispose();

            if (keepWorkingDirectory)
            {
                logger.Warning(
                    "temp.directory.retained",
                    "Se conservaron temporales de documentos fallidos para diagnóstico.",
                    data: new Dictionary<string, object?> { ["working_directory"] = workingDirectory });
            }
            else
            {
                TryDeleteWorkingDirectory(workingDirectory, logger);
            }
        }
    }

    private int ResolveWindowSize()
    {
        var maxInMemory = Math.Max(1, _options.MaxDocumentsInMemory);

        if (_options.PrefetchNextDocumentWindow && maxInMemory > 1)
        {
            // Dos ventanas pueden coexistir: actual + prefetch.
            return Math.Max(1, maxInMemory / 2);
        }

        return maxInMemory;
    }

    private static IReadOnlyList<string> CopyWindow(
        IReadOnlyList<string> source,
        int offset,
        int size)
    {
        var count = Math.Min(size, source.Count - offset);
        var window = new string[count];

        for (var index = 0; index < count; index++)
        {
            window[index] = source[offset + index];
        }

        return window;
    }

    private static async Task InitializeResultsAsync(
        IReadOnlyList<PreparedDocument> documents,
        string outputDirectory,
        ResultWriter resultWriter,
        BalancerLogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            if (document.PreparationError is not null || document.Units.Count == 0)
            {
                continue;
            }

            try
            {
                await resultWriter.InitializeAsync(document, outputDirectory, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                document.PreparationError = $"No fue posible inicializar el resultado: {exception.Message}";
                foreach (var unit in document.Units)
                {
                    unit.FailPermanently(document.PreparationError);
                }

                logger.Error(
                    "result.initialize_failed",
                    "No fue posible crear el archivo de resultado del documento.",
                    exception,
                    new LogContext(DocumentId: document.Id, SourcePath: document.SourcePath));
            }
        }
    }

    private ProcessedDocument BuildProcessedDocument(PreparedDocument document)
    {
        var unitsResolved =
            document.PreparationError is null &&
            document.Units.Count > 0 &&
            document.Units.All(unit => unit.IsCompleted);

        var documentError = document.PreparationError;
        if (!unitsResolved && string.IsNullOrWhiteSpace(documentError))
        {
            documentError = document.Units
                .Where(unit => !unit.IsCompleted)
                .Select(unit => unit.LastError ?? unit.LastFinishReason)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        IReadOnlyList<ProcessingUnitSummary> summaries = Array.Empty<ProcessingUnitSummary>();

        if (_options.RetainUnitSummariesInBatchResult)
        {
            summaries = document.Units.Select(unit => new ProcessingUnitSummary(
                unit.PageNumber,
                unit.IsCompleted,
                unit.VlmAttempts,
                unit.InfrastructureFailures,
                unit.GlobalFailures,
                unit.QueueWait.TotalSeconds,
                unit.LastFinishReason,
                unit.LastModel,
                unit.LastServer,
                unit.LastError)).ToArray();
        }

        int? numPages = document.Units.Count > 0 ? document.Units.Count : null;
        double? processingTime = document.GetProcessingTimeSeconds() is { } seconds
            ? Math.Round(seconds, 3, MidpointRounding.AwayFromZero)
            : null;

        return new ProcessedDocument(
            document.SourcePath,
            document.OutputPath,
            unitsResolved && document.OutputPath is not null,
            numPages,
            processingTime,
            documentError,
            summaries);
    }

    private async Task DispatchAsync(
        IReadOnlyList<PreparedDocument> documents,
        ProcessingRequest request,
        ServerPool serverPool,
        ResultWriter resultWriter,
        BalancerLogger logger,
        CancellationToken cancellationToken)
    {
        var pending = new List<ProcessingUnit>(documents.SelectMany(document => document.Units));
        var documentsById = documents.ToDictionary(document => document.Id);
        var notifiedDocuments = new HashSet<Guid>();

        foreach (var unit in pending)
        {
            unit.EnterQueue();
        }

        logger.Info(
            "scheduler.started",
            $"Scheduler iniciado con {pending.Count} unidad(es).",
            data: new Dictionary<string, object?>
            {
                ["units"] = pending.Count,
                ["servers"] = serverPool.Servers.Count
            });

        if (pending.Count == 0)
        {
            await NotifyResolvedDocumentsAsync(
                documents,
                notifiedDocuments,
                request,
                resultWriter,
                logger,
                cancellationToken);
            return;
        }

        if (request.ModelPriority.Count == 0)
        {
            foreach (var unit in pending)
            {
                unit.FailPermanently("No existe una prioridad de modelos para procesar el lote.");
            }

            logger.Error("scheduler.no_models", "No existe una prioridad de modelos para procesar el lote.");
            await NotifyResolvedDocumentsAsync(
                documents,
                notifiedDocuments,
                request,
                resultWriter,
                logger,
                cancellationToken);
            return;
        }

        var active = new Dictionary<Guid, ActiveDispatch>();
        var lastWaitingLog = DateTimeOffset.MinValue;

        while (pending.Any(unit => !unit.IsCompleted && !unit.IsTerminalFailure) || active.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await DrainCompletedDispatchesAsync(active, pending);
            RemoveTerminalPendingUnits(pending, documentsById, logger);
            await NotifyResolvedDocumentsAsync(
                documents,
                notifiedDocuments,
                request,
                resultWriter,
                logger,
                cancellationToken);

            var dispatchedAny = false;

            // Cada servidor libre toma como máximo una unidad. Todos los nodos
            // pueden despachar en el mismo ciclo, por lo que el paralelismo queda
            // acotado naturalmente al número de VLM Servers disponibles.
            foreach (var server in serverPool.GetFreeServersSnapshot())
            {
                var now = DateTimeOffset.UtcNow;

                var candidate = pending
                    .Where(unit =>
                        !unit.IsCompleted &&
                        !unit.IsTerminalFailure &&
                        unit.NextEligibleAt <= now)
                    .Select(unit => new
                    {
                        Unit = unit,
                        Model = ModelPriorityPolicy.ResolveModel(unit, request.ModelPriority)
                    })
                    .Where(item => item.Model is not null && server.CanDispatch(item.Model))
                    .OrderBy(item => server.HasActiveModel(item.Model!) ? 0 : server.RequiresRecovery() ? 2 : 1)
                    .ThenBy(item => item.Unit.QueueEnteredAt ?? DateTimeOffset.MaxValue)
                    .FirstOrDefault();

                if (candidate is null || !server.Slot.Wait(0))
                {
                    continue;
                }

                var unit = candidate.Unit;
                var model = candidate.Model!;
                pending.Remove(unit);
                unit.MarkAssignment(server.Name, model);
                server.MarkAssigned();
                dispatchedAny = true;

                var document = documentsById[unit.DocumentId];
                var context = CreateUnitContext(document, unit, server.Name, model);

                logger.Info(
                    "unit.dispatched",
                    "Unidad asignada a VLM Server.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["pending_units"] = pending.Count,
                        ["active_units"] = active.Count + 1,
                        ["global_failures"] = unit.GlobalFailures
                    });

                active[unit.Id] = new ActiveDispatch(
                    unit,
                    ProcessOnServerAsync(
                        document,
                        unit,
                        model,
                        request,
                        server,
                        resultWriter,
                        logger,
                        cancellationToken));
            }

            await DrainCompletedDispatchesAsync(active, pending);
            RemoveTerminalPendingUnits(pending, documentsById, logger);

            var unresolved = pending.Any(unit => !unit.IsCompleted && !unit.IsTerminalFailure);
            if (unresolved && !dispatchedAny && DateTimeOffset.UtcNow - lastWaitingLog >= TimeSpan.FromSeconds(30))
            {
                lastWaitingLog = DateTimeOffset.UtcNow;
                var requiredModels = pending
                    .Where(unit => !unit.IsCompleted && !unit.IsTerminalFailure)
                    .Select(unit => ModelPriorityPolicy.ResolveModel(unit, request.ModelPriority))
                    .Where(model => model is not null)
                    .Select(model => model!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                logger.Info(
                    "scheduler.waiting",
                    "Hay unidades esperando capacidad compatible.",
                    data: new Dictionary<string, object?>
                    {
                        ["pending_units"] = pending.Count(unit => !unit.IsTerminalFailure),
                        ["online_servers"] = serverPool.Servers.Count(server => server.IsCurrentlyOnline()),
                        ["free_servers"] = serverPool.Servers.Count(server => server.IsCurrentlyOnline() && server.Slot.CurrentCount > 0),
                        ["required_models"] = requiredModels
                    });
            }

            if (active.Count > 0)
            {
                await Task.WhenAny(
                    active.Values
                        .Select(dispatch => (Task)dispatch.Task)
                        .Append(Task.Delay(_options.SchedulerDelay, cancellationToken)));
            }
            else if (unresolved)
            {
                await Task.Delay(_options.SchedulerDelay, cancellationToken);
            }
        }

        await NotifyResolvedDocumentsAsync(
            documents,
            notifiedDocuments,
            request,
            resultWriter,
            logger,
            cancellationToken);
        logger.Info("scheduler.completed", "Scheduler sin trabajo pendiente.");
    }

    private async Task NotifyResolvedDocumentsAsync(
        IReadOnlyList<PreparedDocument> documents,
        HashSet<Guid> notifiedDocuments,
        ProcessingRequest request,
        ResultWriter resultWriter,
        BalancerLogger logger,
        CancellationToken cancellationToken)
    {
        if (request.DocumentCompleted is null)
        {
            return;
        }

        foreach (var document in documents)
        {
            if (notifiedDocuments.Contains(document.Id))
            {
                continue;
            }

            var resolved = document.PreparationError is not null ||
                           document.Units.Count == 0 ||
                           document.Units.All(unit => unit.IsCompleted || unit.IsTerminalFailure);
            if (!resolved)
            {
                continue;
            }

            document.MarkProcessingCompletedIfResolved();
            var summary = BuildProcessedDocument(document);

            try
            {
                await resultWriter.MarkCompletedAsync(document, summary, cancellationToken);
                await request.DocumentCompleted(summary, cancellationToken);
                notifiedDocuments.Add(document.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.Error(
                    "document.callback_failed",
                    "El consumidor no pudo persistir inmediatamente el documento terminado.",
                    exception,
                    new LogContext(DocumentId: document.Id, SourcePath: document.SourcePath));
            }
        }
    }

    private async Task DrainCompletedDispatchesAsync(
        Dictionary<Guid, ActiveDispatch> active,
        List<ProcessingUnit> pending)
    {
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
                    dispatch.Unit.EnterQueue();
                    pending.Add(dispatch.Unit);
                }
            }
            finally
            {
                active.Remove(completedId);
            }
        }
    }

    private void RemoveTerminalPendingUnits(
        List<ProcessingUnit> pending,
        IReadOnlyDictionary<Guid, PreparedDocument> documentsById,
        BalancerLogger logger)
    {
        foreach (var unit in pending.Where(unit => !unit.IsCompleted && !unit.IsTerminalFailure).ToArray())
        {
            if (_options.MaxGlobalFailuresPerUnit > 0 &&
                unit.GlobalFailures >= _options.MaxGlobalFailuresPerUnit)
            {
                unit.EnforceGlobalFailureLimit(_options.MaxGlobalFailuresPerUnit);
                var document = documentsById[unit.DocumentId];
                logger.Error(
                    "unit.global_failures_exhausted",
                    "La unidad alcanzó el límite global de fallos.",
                    context: CreateUnitContext(document, unit),
                    data: RetryData(unit));
            }
        }

        pending.RemoveAll(unit => unit.IsCompleted || unit.IsTerminalFailure);
    }

    private async Task<DispatchDisposition> ProcessOnServerAsync(
        PreparedDocument document,
        ProcessingUnit unit,
        string model,
        ProcessingRequest request,
        Runtime server,
        ResultWriter resultWriter,
        BalancerLogger logger,
        CancellationToken cancellationToken)
    {
        var context = CreateUnitContext(document, unit, server.Name, model);
        var attemptStarted = Stopwatch.GetTimestamp();
        var attemptSuccess = false;
        string? attemptFinishReason = null;
        string? attemptTranscription = null;
        string? attemptError = null;
        double? attemptProcessingSeconds = null;

        try
        {
            // Verificación justo antes de operar: evita actuar con un estado
            // obsoleto entre el heartbeat y la asignación.
            var status = await server.Client.GetStatusAsync(
                _options.StatusRequestTimeout,
                cancellationToken,
                context);
            server.UpdateStatus(status, _options.StuckStateTimeout);

            if (server.NeedsModelPreparation(model))
            {
                await server.Client.EnsureModelAsync(
                    model,
                    status,
                    _options.ModelCommandTimeout,
                    cancellationToken,
                    context);

                server.MarkModelReady(model);
            }

            // ProcessingTime comienza aquí: el servidor y el modelo ya están
            // listos y la primera página/unidad entra realmente al VLM.
            document.MarkProcessingStarted();

            var response = await server.Client.ProcessImageAsync(
                unit.ImagePath,
                _options.ProcessingRequestTimeout,
                cancellationToken,
                context);

            server.MarkModelReady(model);
            attemptFinishReason = response.FinishReason;
            attemptProcessingSeconds = response.ProcessingSeconds;

            // Toda respuesta documental actualiza inmediatamente el JSON. Un
            // error deja la unidad vacía; stop/length guardan contenido saneado.
            var acceptedContent = response.FinishReason is "stop" or "length" or "lenght"
                ? response.Content
                : string.Empty;

            await resultWriter.UpdateUnitAsync(
                document,
                unit,
                acceptedContent,
                cancellationToken);

            switch (response.FinishReason)
            {
                case "stop":
                case "length":
                case "lenght":
                    attemptSuccess = true;
                    attemptTranscription = acceptedContent;
                    unit.Complete(response);
                    logger.Info(
                        "unit.completed",
                        response.FinishReason.Equals("stop", StringComparison.OrdinalIgnoreCase)
                            ? "Unidad procesada correctamente."
                            : "Unidad aceptada con finish_reason=length; no requiere reintento.",
                        context,
                        new Dictionary<string, object?>
                        {
                            ["finish_reason"] = response.FinishReason,
                            ["processing_seconds"] = response.ProcessingSeconds,
                            ["vlm_failures"] = unit.VlmAttempts,
                            ["infrastructure_failures"] = unit.InfrastructureFailures,
                            ["global_failures"] = unit.GlobalFailures
                        });
                    return DispatchDisposition.Done;

                case "error":
                    attemptError = "El VLM retornó finish_reason=error.";
                    unit.RegisterVlmFailure(
                        response,
                        request.MaxAttempts,
                        request.ModelPriority.Count);
                    unit.EnforceGlobalFailureLimit(_options.MaxGlobalFailuresPerUnit);
                    unit.NextEligibleAt = unit.IsTerminalFailure
                        ? DateTimeOffset.MaxValue
                        : DateTimeOffset.UtcNow;

                    logger.Warning(
                        unit.IsTerminalFailure ? "unit.vlm_attempts_exhausted" : "unit.vlm_retry",
                        unit.IsTerminalFailure
                            ? "La unidad agotó el presupuesto de errores VLM/global."
                            : "finish_reason=error; la unidad avanzará al siguiente modelo.",
                        context,
                        new Dictionary<string, object?>
                        {
                            ["finish_reason"] = response.FinishReason,
                            ["vlm_failures"] = unit.VlmAttempts,
                            ["max_vlm_attempts"] = request.MaxAttempts,
                            ["global_failures"] = unit.GlobalFailures,
                            ["next_model_priority_index"] = unit.ModelPriorityIndex
                        });

                    return unit.IsTerminalFailure
                        ? DispatchDisposition.Done
                        : DispatchDisposition.Requeue;

                default:
                    attemptError = $"finish_reason no reconocido: '{response.FinishReason}'.";
                    RegisterInfrastructureFailure(
                        unit,
                        attemptError);
                    logger.Warning(
                        "unit.processing_technical_failure",
                        "finish_reason no reconocido; se mantiene el mismo modelo.",
                        context,
                        RetryData(unit));
                    return unit.IsTerminalFailure
                        ? DispatchDisposition.Done
                        : DispatchDisposition.Requeue;
            }
        }
        catch (VlmServerRecoveryException exception)
        {
            attemptFinishReason = "server_recovery_error";
            attemptError = exception.Message;
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            RegisterInfrastructureFailure(unit, exception.Message);
            logger.Error(
                "unit.server_recovery_failed",
                "Falló la recuperación administrativa del servidor; la unidad podrá migrar.",
                exception,
                context,
                RetryData(unit));
            return unit.IsTerminalFailure ? DispatchDisposition.Done : DispatchDisposition.Requeue;
        }
        catch (VlmModelUnavailableException exception)
        {
            attemptFinishReason = "model_unavailable";
            attemptError = exception.Message;
            server.BlockModel(model, _options.ModelFailureCooldown);
            RegisterInfrastructureFailure(unit, exception.Message);
            logger.Error(
                "unit.model_prepare_failed",
                "El modelo no pudo prepararse en este servidor; la unidad podrá usar otro nodo con el mismo modelo.",
                exception,
                context,
                RetryData(unit));
            return unit.IsTerminalFailure ? DispatchDisposition.Done : DispatchDisposition.Requeue;
        }
        catch (VlmServerBusyException exception)
        {
            attemptFinishReason = "server_busy";
            attemptError = exception.Message;
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            unit.Delay(exception.Message, _options.SchedulerDelay);
            return DispatchDisposition.Requeue;
        }
        catch (VlmProcessingTechnicalException exception)
        {
            attemptFinishReason = "processing_technical_error";
            attemptError = exception.Message;
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            RegisterInfrastructureFailure(unit, exception.Message);
            logger.Warning(
                "unit.processing_technical_failure",
                "La API no entregó un resultado documental utilizable; se mantiene el modelo.",
                context,
                RetryData(unit, exception.Message));
            return unit.IsTerminalFailure ? DispatchDisposition.Done : DispatchDisposition.Requeue;
        }
        catch (VlmServerTransientException exception)
        {
            attemptFinishReason = "server_transient_error";
            attemptError = exception.Message;
            if (exception.StatusCode is null)
            {
                var transitioned = server.MarkOffline(exception.Message, _options.ServerCooldown);
                if (transitioned)
                {
                    logger.Warning(
                        "server.offline",
                        $"Servidor fuera de línea: {server.Name}.",
                        context,
                        new Dictionary<string, object?> { ["error"] = exception.Message });
                }
            }
            else
            {
                server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            }

            RegisterInfrastructureFailure(unit, exception.Message);
            logger.Warning(
                "unit.infrastructure_failure",
                "Fallo de infraestructura; la unidad conserva el modelo y podrá migrar a otro nodo.",
                context,
                RetryData(unit, exception.Message));
            return unit.IsTerminalFailure ? DispatchDisposition.Done : DispatchDisposition.Requeue;
        }
        catch (IOException exception)
        {
            attemptFinishReason = "local_io_error";
            attemptError = exception.Message;
            unit.FailPermanently(exception.Message);
            logger.Error("unit.local_io_error", "Falló el acceso a un archivo local.", exception, context);
            return DispatchDisposition.Done;
        }
        catch (UnauthorizedAccessException exception)
        {
            attemptFinishReason = "local_access_error";
            attemptError = exception.Message;
            unit.FailPermanently(exception.Message);
            logger.Error("unit.local_access_error", "No existe permiso para acceder a un archivo local.", exception, context);
            return DispatchDisposition.Done;
        }
        catch (VlmRequestException exception)
        {
            attemptFinishReason = "request_rejected";
            attemptError = exception.Message;
            unit.FailPermanently(exception.Message);
            logger.Error("unit.request_rejected", "La API rechazó la solicitud documental.", exception, context);
            return DispatchDisposition.Done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            attemptFinishReason = "unexpected_error";
            attemptError = exception.Message;
            server.MarkTransientFailure(exception.Message, _options.ServerCooldown);
            RegisterInfrastructureFailure(unit, exception.Message);
            logger.Error(
                "unit.unexpected_error",
                "Error inesperado; la unidad se reencolará si conserva presupuesto global.",
                exception,
                context,
                RetryData(unit));
            return unit.IsTerminalFailure ? DispatchDisposition.Done : DispatchDisposition.Requeue;
        }
        finally
        {
            document.MarkProcessingCompletedIfResolved();

            if (attemptProcessingSeconds is null)
            {
                attemptProcessingSeconds = Stopwatch.GetElapsedTime(attemptStarted).TotalSeconds;
            }

            await NotifyAttemptCompletedAsync(
                request,
                new ProcessingAttemptEvent(
                    document.SourcePath,
                    unit.PageNumber ?? 0,
                    unit.AttemptNumber,
                    model,
                    server.Name,
                    attemptSuccess,
                    attemptFinishReason,
                    attemptProcessingSeconds,
                    DateTimeOffset.Now,
                    attemptTranscription,
                    attemptError),
                logger);

            server.Slot.Release();
        }
    }

    private static async Task NotifyAttemptCompletedAsync(
        ProcessingRequest request,
        ProcessingAttemptEvent attempt,
        BalancerLogger logger)
    {
        if (request.AttemptCompleted is null)
        {
            return;
        }

        try
        {
            // La persistencia histórica no debe quedar cancelada a mitad de una
            // escritura que corresponde a un intento ya finalizado.
            await request.AttemptCompleted(attempt, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.Error(
                "unit.callback_failed",
                "No fue posible persistir el historial incremental del intento.",
                exception,
                new LogContext(
                    SourcePath: attempt.SourcePath,
                    PageNumber: attempt.UnitIndex == 0 ? null : attempt.UnitIndex,
                    Server: attempt.Server,
                    Model: attempt.Model));
        }
    }

    private void RegisterInfrastructureFailure(ProcessingUnit unit, string error)
    {
        unit.RegisterInfrastructureFailure(
            error,
            _options.SchedulerDelay,
            _options.MaxInfrastructureFailuresPerUnit);
        unit.EnforceGlobalFailureLimit(_options.MaxGlobalFailuresPerUnit);
    }

    private static IReadOnlyDictionary<string, object?> RetryData(
        ProcessingUnit unit,
        string? reason = null) =>
        new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["vlm_failures"] = unit.VlmAttempts,
            ["infrastructure_failures"] = unit.InfrastructureFailures,
            ["global_failures"] = unit.GlobalFailures,
            ["terminal"] = unit.IsTerminalFailure,
            ["next_eligible_at"] = unit.NextEligibleAt
        };

    private static LogContext CreateUnitContext(
        PreparedDocument document,
        ProcessingUnit unit,
        string? server = null,
        string? model = null) =>
        new(
            DocumentId: document.Id,
            UnitId: unit.Id,
            SourcePath: document.SourcePath,
            PageNumber: unit.PageNumber,
            TemporaryPath: unit.ImagePath,
            Server: server,
            Model: model);


    private static void LogMemorySnapshot(
        BalancerLogger logger,
        int windowNumber,
        string phase)
    {
        const double bytesPerMb = 1024d * 1024d;
        logger.Info(
            "memory.snapshot",
            "Muestra de memoria del proceso.",
            data: new Dictionary<string, object?>
            {
                ["window"] = windowNumber,
                ["phase"] = phase,
                ["managed_mb"] = Math.Round(GC.GetTotalMemory(forceFullCollection: false) / bytesPerMb, 1),
                ["working_set_mb"] = Math.Round(Environment.WorkingSet / bytesPerMb, 1)
            });
    }

    private enum DispatchDisposition
    {
        Done,
        Requeue
    }

    private sealed record ActiveDispatch(
        ProcessingUnit Unit,
        Task<DispatchDisposition> Task);

    private static void TryDeleteDocumentTemporaryDirectory(
        PreparedDocument document,
        BalancerLogger logger)
    {
        if (document.Type != "pdf" || string.IsNullOrWhiteSpace(document.StateDirectory))
        {
            return;
        }

        try
        {
            if (Directory.Exists(document.StateDirectory))
            {
                Directory.Delete(document.StateDirectory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            logger.Warning(
                "temp.document.delete_failed",
                $"No fue posible eliminar temporales de {Path.GetFileName(document.SourcePath)}: {exception.Message}",
                new LogContext(DocumentId: document.Id, SourcePath: document.SourcePath));
        }
    }

    private static void TryDeleteWorkingDirectory(string path, BalancerLogger logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            logger.Warning(
                "temp.directory.delete_failed",
                $"No fue posible eliminar el directorio temporal: {exception.Message}",
                data: new Dictionary<string, object?> { ["working_directory"] = path });
        }
    }
}
