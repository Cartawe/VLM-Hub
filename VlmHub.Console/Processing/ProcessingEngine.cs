using System.Collections.Concurrent;
using System.Text.Json;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;
using VlmHub.Balancer.Orchestration;
using VlmHub.Sql;
using VlmHub.Sql.Models;
using VlmHub.Sql.Persistence;

namespace VlmHub.Console.Processing;

internal sealed class ProcessingEngine : IDisposable
{
    private readonly ProcessingWorkspace _workspace;
    private readonly ProcessingRunControl _run;
    private readonly SqlServer _sql;
    private readonly IReadOnlyList<ColumnInfo> _primaryKeyColumns;
    private readonly LocalHistoryStore _history;
    private readonly VlmHubSettings _settings;
    private readonly DocumentDownloader _downloader;
    private readonly ProcessingCoordinator _coordinator;
    private readonly VlmHubLogger _logger;
    private DateTimeOffset _remoteUploadBackoffUntil = DateTimeOffset.MinValue;

    public ProcessingEngine(
        ProcessingWorkspace workspace,
        ProcessingRunControl run,
        SqlServer sql,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        LocalHistoryStore history,
        VlmHubSettings settings,
        VlmHubLogger logger)
    {
        _workspace = workspace;
        _run = run;
        _sql = sql;
        _primaryKeyColumns = primaryKeyColumns;
        _history = history;
        _settings = settings;
        _logger = logger;
        _downloader = new DocumentDownloader(settings);
        _coordinator = new ProcessingCoordinator(
            settings.ToBalancerOptions(Path.Combine(Environment.CurrentDirectory, "logs"), logger));
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var window = new List<SelectionManifestItem>(Math.Max(1, _settings.MaxDocumentsInMemory));

        await foreach (var item in SelectionManifestReader.ReadAsync(
                           _workspace.SelectionPath,
                           cancellationToken))
        {
            var control = await ReadOrCreateControlAsync(item.ItemId, cancellationToken);
            if (control.State == ProcessingStates.Completed)
            {
                continue;
            }

            if (control.State == ProcessingStates.PendingUpload)
            {
                await RepairPendingUploadHistoryAsync(item, control, cancellationToken);
                await PersistPendingUploadAsync(item, control, cancellationToken);
                continue;
            }

            window.Add(item);
            if (window.Count >= Math.Max(1, _settings.MaxDocumentsInMemory))
            {
                await ProcessWindowAsync(window, cancellationToken);
                window.Clear();
            }
        }

        if (window.Count > 0)
        {
            await ProcessWindowAsync(window, cancellationToken);
        }

        await FlushPendingUploadsAsync(cancellationToken);
        await FinalizeRunIfCompleteAsync(cancellationToken);
    }

    private async Task ProcessWindowAsync(
        IReadOnlyList<SelectionManifestItem> items,
        CancellationToken cancellationToken)
    {
        var localByPath = new ConcurrentDictionary<string, SelectionManifestItem>(
            StringComparer.OrdinalIgnoreCase);
        var startedByItem = new ConcurrentDictionary<Guid, DateTimeOffset>();
        var downloadFailures = new ConcurrentBag<(SelectionManifestItem Item, Exception Error)>();
        using var gate = new SemaphoreSlim(Math.Max(1, _settings.MaxParallelDownloads));

        var downloadTasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var startedAt = DateTimeOffset.Now;
                startedByItem[item.ItemId] = startedAt;
                await SetProcessingAsync(item, startedAt, cancellationToken);
                _logger.Info(
                    "DOWNLOADER",
                    "document.download_started",
                    "Iniciando obtención del documento.",
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));

                if (string.IsNullOrWhiteSpace(item.DocumentUrl))
                {
                    throw new IOException("La referencia documental almacenada en SQL Server es NULL o vacía.");
                }

                var localPath = await _downloader.DownloadAsync(
                    item.DocumentUrl,
                    _workspace.TempBasePath(item.ItemId),
                    cancellationToken);
                localByPath[Path.GetFullPath(localPath)] = item;
                _logger.Info(
                    "DOWNLOADER",
                    "document.download_completed",
                    "Documento descargado a temporal local.",
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error(
                    "DOWNLOADER",
                    "document.download_failed",
                    "Falló la obtención del documento.",
                    exception,
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));
                downloadFailures.Add((item, exception));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(downloadTasks);

        foreach (var failure in downloadFailures)
        {
            await CompleteWithoutVlmAsync(
                failure.Item,
                failure.Error,
                startedByItem[failure.Item.ItemId],
                cancellationToken);
        }

        if (localByPath.IsEmpty)
        {
            return;
        }

        foreach (var pair in localByPath)
        {
            _logger.Info(
                "CONSOLE",
                "document.processing_started",
                "Documento entregado al balanceador.",
                _run.ProcessingId,
                pair.Value.ItemId,
                PrimaryKeyLabel(pair.Value));
        }

        var request = new ProcessingRequest
        {
            Documents = localByPath.Keys.ToArray(),
            ModelPriority = _settings.ModelPriority,
            MaxAttempts = _settings.MaxAttempts,
            ServersFile = _settings.ResolveServersFile(),
            OutputDirectory = _workspace.ResultsDirectory,
            ProcessingId = _run.ProcessingId,
            AttemptCompleted = async (attempt, _) =>
            {
                if (!localByPath.TryGetValue(Path.GetFullPath(attempt.SourcePath), out var item))
                {
                    return;
                }

                try
                {
                    await _history.AddAttemptAsync(
                        new ProcessingUnitAttemptRecord(
                            item.ItemId,
                            attempt.UnitIndex,
                            attempt.AttemptNumber,
                            attempt.Model,
                            attempt.Server,
                            attempt.Success,
                            attempt.FinishReason,
                            attempt.ProcessingSeconds,
                            attempt.ProcessedAt,
                            attempt.Transcription,
                            attempt.Error),
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        "SQLITE",
                        "sqlite.write_failed",
                        "No fue posible registrar el intento de unidad.",
                        exception,
                        _run.ProcessingId,
                        item.ItemId,
                        PrimaryKeyLabel(item),
                        attempt.Server,
                        attempt.Model);
                }
            },
            DocumentCompleted = async (document, _) =>
            {
                if (!localByPath.TryGetValue(Path.GetFullPath(document.SourcePath), out var item))
                {
                    return;
                }

                var finishedAt = DateTimeOffset.Now;

                try
                {
                    await _history.CompleteDocumentAsync(
                        item.ItemId,
                        document.Completed,
                        finishedAt,
                        document.ProcessingTime,
                        cancellationToken: CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        "SQLITE",
                        "sqlite.write_failed",
                        "No fue posible cerrar el historial del documento.",
                        exception,
                        _run.ProcessingId,
                        item.ItemId,
                        PrimaryKeyLabel(item));
                }

                var control = await ReadOrCreateControlAsync(item.ItemId, CancellationToken.None);
                control.State = ProcessingStates.PendingUpload;
                control.NumPages = document.NumPages;
                control.Success = document.Completed;
                control.ResultPath = document.OutputPath is null
                    ? null
                    : Path.GetRelativePath(_workspace.RootDirectory, document.OutputPath)
                        .Replace('\\', '/');
                control.LastError = document.Error;
                control.ProcessingTime = document.ProcessingTime;
                control.UpdatedAt = finishedAt;
                await AtomicJsonFile.WriteAsync(
                    _workspace.ItemControlPath(item.ItemId),
                    control,
                    CancellationToken.None);
                _logger.Info(
                    "CONSOLE",
                    "document.result_ready",
                    "Resultado local finalizado; pendiente de persistencia remota.",
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));

                await PersistPendingUploadAsync(item, control, CancellationToken.None);
            }
        };

        var result = await _coordinator.ProcessAsync(request, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.FatalError))
        {
            foreach (var item in localByPath.Values)
            {
                var control = await ReadOrCreateControlAsync(item.ItemId, CancellationToken.None);
                if (control.State == ProcessingStates.Processing)
                {
                    control.State = ProcessingStates.Pending;
                    control.LastError = result.FatalError;
                    control.UpdatedAt = DateTimeOffset.Now;
                    await AtomicJsonFile.WriteAsync(
                        _workspace.ItemControlPath(item.ItemId),
                        control,
                        CancellationToken.None);
                }
            }

            throw new InvalidOperationException(result.FatalError);
        }
    }

    private async Task SetProcessingAsync(
        SelectionManifestItem item,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var control = await ReadOrCreateControlAsync(item.ItemId, cancellationToken);
        control.State = ProcessingStates.Processing;
        control.Success = null;
        control.ResultPath = null;
        control.LastError = null;
        control.ProcessingTime = null;
        control.UpdatedAt = startedAt;
        await AtomicJsonFile.WriteAsync(_workspace.ItemControlPath(item.ItemId), control, cancellationToken);

        try
        {
            await _history.BeginDocumentAsync(
                new LocalDocumentRecord(
                    item.ItemId,
                    _run.ProcessingId,
                    _run.Server,
                    _run.Database,
                    _run.Table,
                    JsonSerializer.Serialize(item.PrimaryKey),
                    GetDocumentName(item.DocumentUrl),
                    startedAt),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "SQLITE",
                "sqlite.write_failed",
                "No fue posible abrir el historial del documento.",
                exception,
                _run.ProcessingId,
                item.ItemId,
                PrimaryKeyLabel(item));
        }
    }

    private async Task CompleteWithoutVlmAsync(
        SelectionManifestItem item,
        Exception error,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var finishedAt = DateTimeOffset.Now;
        try
        {
            await _history.CompleteDocumentAsync(
                item.ItemId,
                false,
                finishedAt,
                processingTime: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "SQLITE",
                "sqlite.write_failed",
                "No fue posible cerrar el historial del fallo de descarga.",
                exception,
                _run.ProcessingId,
                item.ItemId,
                PrimaryKeyLabel(item));
        }

        var control = await ReadOrCreateControlAsync(item.ItemId, cancellationToken);
        control.State = ProcessingStates.PendingUpload;
        control.Success = false;
        control.ResultPath = null;
        control.LastError = error.GetBaseException().Message;
        control.ProcessingTime = null;
        control.UpdatedAt = finishedAt;
        await AtomicJsonFile.WriteAsync(_workspace.ItemControlPath(item.ItemId), control, cancellationToken);
        await PersistPendingUploadAsync(item, control, cancellationToken);
    }

    private async Task PersistPendingUploadAsync(
        SelectionManifestItem item,
        ProcessingItemControl control,
        CancellationToken cancellationToken)
    {
        if (control.State != ProcessingStates.PendingUpload ||
            DateTimeOffset.UtcNow < _remoteUploadBackoffUntil ||
            string.IsNullOrWhiteSpace(_run.AuxiliaryTable))
        {
            return;
        }

        var attempts = Math.Max(1, _settings.SqlUploadMaxAttempts);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.Info(
                    "SQL",
                    "database.upload_started",
                    "Persistiendo resultado documental en SQL Server.",
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));

                var absoluteResultPath = string.IsNullOrWhiteSpace(control.ResultPath)
                    ? null
                    : Path.GetFullPath(Path.Combine(
                        _workspace.RootDirectory,
                        control.ResultPath.Replace('/', Path.DirectorySeparatorChar)));

                var serverId = await _sql.UpsertProcessingResultAsync(
                    _run.AuxiliaryTable,
                    _primaryKeyColumns,
                    item.PrimaryKey,
                    item.ItemId,
                    absoluteResultPath,
                    control.Success == true,
                    control.NumPages,
                    control.UpdatedAt,
                    control.ProcessingTime,
                    control.LastError,
                    cancellationToken);

                try
                {
                    await _history.SetServerProcessingIdAsync(item.ItemId, serverId, cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.Error(
                        "SQLITE",
                        "sqlite.write_failed",
                        "El resultado remoto existe, pero no se pudo guardar su Id en SQLite.",
                        exception,
                        _run.ProcessingId,
                        item.ItemId,
                        PrimaryKeyLabel(item));
                }

                control.ServerProcessingId = serverId;
                control.State = ProcessingStates.Completed;
                control.UpdatedAt = DateTimeOffset.Now;
                await AtomicJsonFile.WriteAsync(
                    _workspace.ItemControlPath(item.ItemId),
                    control,
                    cancellationToken);

                DeleteDownloadedTemporary(item.ItemId);
                DeleteResultCompletionMarker(item.ItemId);
                _remoteUploadBackoffUntil = DateTimeOffset.MinValue;
                _logger.Info(
                    "SQL",
                    "database.upload_completed",
                    $"Resultado persistido con Id={serverId}.",
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                _logger.Error(
                    "SQL",
                    "database.upload_failed",
                    $"Falló la persistencia remota (intento {attempt}/{attempts}).",
                    exception,
                    _run.ProcessingId,
                    item.ItemId,
                    PrimaryKeyLabel(item));
                if (attempt < attempts)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, _settings.SqlUploadRetrySeconds)),
                        cancellationToken);
                }
            }
        }

        // Circuit breaker pequeño: cuando SQL está caído, no bloqueamos cada
        // documento durante varios reintentos. El resultado queda PendingUpload.
        _remoteUploadBackoffUntil = DateTimeOffset.UtcNow.AddSeconds(
            Math.Max(1, _settings.SqlUploadCooldownSeconds));
        _ = lastError;
    }

    private async Task FlushPendingUploadsAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in SelectionManifestReader.ReadAsync(
                           _workspace.SelectionPath,
                           cancellationToken))
        {
            if (DateTimeOffset.UtcNow < _remoteUploadBackoffUntil)
            {
                return;
            }

            var control = await ReadOrCreateControlAsync(item.ItemId, cancellationToken);
            if (control.State == ProcessingStates.PendingUpload)
            {
                await PersistPendingUploadAsync(item, control, cancellationToken);
            }
        }
    }

    private async Task FinalizeRunIfCompleteAsync(CancellationToken cancellationToken)
    {
        long completed = 0;
        long success = 0;
        long failed = 0;

        await foreach (var item in SelectionManifestReader.ReadAsync(
                           _workspace.SelectionPath,
                           cancellationToken))
        {
            var control = await ReadOrCreateControlAsync(item.ItemId, cancellationToken);
            if (control.State != ProcessingStates.Completed)
            {
                _run.Status = "processing";
                await AtomicJsonFile.WriteAsync(_workspace.RunPath, _run, cancellationToken);
                return;
            }

            completed++;
            if (control.Success == true) success++; else failed++;
        }

        var finishedAt = DateTimeOffset.Now;
        _run.Status = "completed";
        _run.Total = completed;
        _run.Success = success;
        _run.Failed = failed;
        _run.FinishedAt = finishedAt;
        _run.DurationSeconds = _run.StartedAt is { } started
            ? (finishedAt - started).TotalSeconds
            : null;
        await AtomicJsonFile.WriteAsync(_workspace.RunPath, _run, cancellationToken);
    }

    private async Task RepairPendingUploadHistoryAsync(
        SelectionManifestItem item,
        ProcessingItemControl control,
        CancellationToken cancellationToken)
    {
        try
        {
            await _history.BeginDocumentAsync(
                new LocalDocumentRecord(
                    item.ItemId,
                    _run.ProcessingId,
                    _run.Server,
                    _run.Database,
                    _run.Table,
                    JsonSerializer.Serialize(item.PrimaryKey),
                    GetDocumentName(item.DocumentUrl),
                    _run.StartedAt ?? _run.CreatedAt),
                cancellationToken);
            await _history.CompleteDocumentAsync(
                item.ItemId,
                control.Success == true,
                control.UpdatedAt,
                control.ProcessingTime,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "SQLITE",
                "sqlite.recovery_write_failed",
                "No fue posible reconciliar Document antes de reintentar la subida SQL.",
                exception,
                _run.ProcessingId,
                item.ItemId,
                PrimaryKeyLabel(item));
        }
    }

    private void DeleteResultCompletionMarker(Guid itemId)
    {
        try
        {
            var marker = $"{_workspace.ResultPath(itemId)}.ready";
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch
        {
            // Es auxiliar de recuperación; Completed ya está persistido en SQL.
        }
    }

    private void DeleteDownloadedTemporary(Guid itemId)
    {
        foreach (var path in Directory.EnumerateFiles(
                     _workspace.TempDirectory,
                     $"{itemId:N}.*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // El resultado ya está persistido; la limpieza no debe revertir
                // el estado Completed.
            }
        }
    }

    private async Task<ProcessingItemControl> ReadOrCreateControlAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var path = _workspace.ItemControlPath(itemId);
        if (File.Exists(path))
        {
            var existing = await AtomicJsonFile.ReadAsync<ProcessingItemControl>(path, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var control = new ProcessingItemControl
        {
            ItemId = itemId,
            State = ProcessingStates.Pending,
            UpdatedAt = DateTimeOffset.Now
        };
        await AtomicJsonFile.WriteAsync(path, control, cancellationToken);
        return control;
    }

    private static string? GetDocumentName(string? documentUrl)
    {
        if (string.IsNullOrWhiteSpace(documentUrl))
        {
            return null;
        }

        if (Uri.TryCreate(documentUrl, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        var fallback = Path.GetFileName(documentUrl.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    private static string PrimaryKeyLabel(SelectionManifestItem item) =>
        JsonSerializer.Serialize(item.PrimaryKey);

    public void Dispose() => _downloader.Dispose();
}
