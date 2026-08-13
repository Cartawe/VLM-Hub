using VlmHub.Balancer.Logging;
using VlmHub.Sql;
using VlmHub.Sql.Persistence;

namespace VlmHub.Console.Processing;

/// <summary>
/// Modo interno sin interfaz de usuario. Ejecuta una selección ya congelada y
/// usa únicamente datos de control + credenciales heredadas por entorno.
/// </summary>
internal static class ProcessingWorker
{
    internal const string HostEnvironment = "VLMHUB_WORKER_SQL_HOST";
    internal const string UserEnvironment = "VLMHUB_WORKER_SQL_USER";
    internal const string PasswordEnvironment = "VLMHUB_WORKER_SQL_PASSWORD";

    public static async Task<int> RunAsync(
        string processingDirectory,
        VlmHubLogger logger,
        CancellationToken processCancellationToken = default)
    {
        var root = Path.GetFullPath(processingDirectory);
        var runPath = Path.Combine(root, "control", "run.json");
        var run = await AtomicJsonFile.ReadAsync<ProcessingRunControl>(
            runPath,
            processCancellationToken)
            ?? throw new InvalidOperationException("No fue posible leer control/run.json.");
        var workspace = ProcessingWorkspace.Open(run.ProcessingId, root);

        FileStream workerLock;
        try
        {
            workerLock = new FileStream(
                workspace.WorkerLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.None);
        }
        catch (IOException)
        {
            logger.Warning(
                "WORKER",
                "worker.already_running",
                "La ejecución ya tiene un worker activo.",
                run.ProcessingId);
            return 4;
        }

        await using (workerLock)
        {
            await File.WriteAllTextAsync(
                workspace.WorkerPidPath,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None);

            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(processCancellationToken);
            var cancelWatch = WatchCancellationRequestAsync(workspace, run, logger, workerCts);

            try
            {
                var host = Environment.GetEnvironmentVariable(HostEnvironment);
                var user = Environment.GetEnvironmentVariable(UserEnvironment);
                var password = Environment.GetEnvironmentVariable(PasswordEnvironment);
                if (string.IsNullOrWhiteSpace(host) ||
                    string.IsNullOrWhiteSpace(user) ||
                    password is null)
                {
                    throw new InvalidOperationException(
                        "El worker no recibió las credenciales temporales de SQL Server.");
                }

                logger.Info(
                    "WORKER",
                    "worker.started",
                    "Worker desacoplado iniciado.",
                    run.ProcessingId,
                    server: host);

                var sql = new SqlServer(host, user, password);
                sql.SetDatabase(run.Database);

                var columns = await sql.GetColumnsAsync(run.Table, workerCts.Token);
                var primaryKeyColumns = run.PrimaryKeyColumns
                    .Select(name => columns.First(column =>
                        string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();

                run.AuxiliaryTable = await sql.EnsureAuxiliaryTableAsync(
                    run.Table,
                    primaryKeyColumns,
                    workerCts.Token);
                run.Status = "processing";
                run.LastError = null;
                run.StartedAt ??= DateTimeOffset.Now;
                await AtomicJsonFile.WriteAsync(workspace.RunPath, run, workerCts.Token);

                // Reconciliar siempre es seguro e idempotente. Esto cubre tanto
                // una recuperación explícita como una caída entre escrituras.
                await ProcessingRecovery.ReconcileAsync(workspace, workerCts.Token);

                var history = new LocalHistoryStore(
                    Path.Combine(Environment.CurrentDirectory, "data", "vlmhub.db"));
                await history.InitializeAsync(workerCts.Token);
                var settings = await VlmHubSettings.LoadAsync(workerCts.Token);

                using var engine = new ProcessingEngine(
                    workspace,
                    run,
                    sql,
                    primaryKeyColumns,
                    history,
                    settings,
                    logger);
                await engine.ProcessPendingAsync(workerCts.Token);

                logger.Info(
                    "WORKER",
                    "worker.completed",
                    "Worker terminó su pasada de procesamiento.",
                    run.ProcessingId);
                return 0;
            }
            catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
            {
                run.Status = "processing";
                run.LastError = "Procesamiento cancelado por el usuario.";
                await TryWriteRunAsync(workspace.RunPath, run);
                logger.Warning(
                    "WORKER",
                    "worker.cancelled",
                    "Worker cancelado; el estado queda recuperable.",
                    run.ProcessingId);
                return 2;
            }
            catch (Exception exception)
            {
                run.Status = "processing";
                run.LastError = exception.GetBaseException().Message;
                await TryWriteRunAsync(workspace.RunPath, run);
                logger.Error(
                    "WORKER",
                    "worker.failed",
                    "El worker no pudo continuar; la ejecución queda recuperable.",
                    exception,
                    run.ProcessingId,
                    server: run.Server);
                return 1;
            }
            finally
            {
                workerCts.Cancel();
                try
                {
                    await cancelWatch;
                }
                catch (OperationCanceledException)
                {
                    // Cierre normal del observador.
                }

                TryDelete(workspace.WorkerPidPath);
            }
        }
    }

    private static async Task WatchCancellationRequestAsync(
        ProcessingWorkspace workspace,
        ProcessingRunControl run,
        VlmHubLogger logger,
        CancellationTokenSource workerCts)
    {
        try
        {
            while (!workerCts.IsCancellationRequested)
            {
                if (File.Exists(workspace.CancelRequestPath))
                {
                    logger.Warning(
                        "WORKER",
                        "worker.cancel_requested",
                        "Se recibió una solicitud explícita de cancelación.",
                        run.ProcessingId);
                    workerCts.Cancel();
                    return;
                }

                await Task.Delay(250, workerCts.Token);
            }
        }
        catch (OperationCanceledException) when (workerCts.IsCancellationRequested)
        {
            // Cierre normal.
        }
    }

    private static async Task TryWriteRunAsync(string path, ProcessingRunControl run)
    {
        try
        {
            await AtomicJsonFile.WriteAsync(path, run, CancellationToken.None);
        }
        catch
        {
            // El control existente continúa siendo recuperable.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Archivo auxiliar; no altera la fuente de verdad de la ejecución.
        }
    }
}
