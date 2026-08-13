using VlmHub.Balancer.Logging;
using VlmHub.Console.Processing;
using VlmHub.Console.Views;
using VlmHub.Sql;
using VlmHub.Sql.Models;

namespace VlmHub.Console.App;

/// <summary>
/// Orquestador único de VLMHub. Los controles locales gobiernan recuperación;
/// SQL gobierna selección/persistencia y Balancer gobierna inferencia.
/// </summary>
public sealed class VlmHubConsoleApp
{
    private readonly VlmHubLogger _logger;

    public VlmHubConsoleApp(VlmHubLogger logger)
    {
        _logger = logger;
    }

    private static readonly string ProcessingRoot =
        Path.Combine(Environment.CurrentDirectory, "processing");

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var option = ConsoleUi.PromptStartOption();
            switch (option)
            {
                case 0:
                    return;
                case 1:
                    await RunNewProcessingAsync(cancellationToken);
                    break;
                case 2:
                    await RunRecoveryAsync(cancellationToken);
                    break;
            }
        }
    }

    private async Task RunNewProcessingAsync(CancellationToken cancellationToken)
    {
        var (server, databases, credentials) = await ConnectAsync(cancellationToken);

        // El login ocurre una sola vez para esta selección. Desde aquí se puede
        // retroceder sin volver a pedir credenciales.
        while (true)
        {
            ConsoleUi.ShowNamedItems("Bases de datos disponibles", databases, "#");
            var database = ConsoleUi.PromptNamedItem(
                databases,
                "Selecciona una base de datos por índice o nombre:")!;
            server.SetDatabase(database);

            IReadOnlyList<string> tables;
            try
            {
                tables = await ConsoleUi.ShowStatusAsync(
                    $"[grey]Consultando tablas de {MarkupEscape(database)}...[/]",
                    () => server.GetTablesAsync(cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra base de datos.");
                continue;
            }

            if (tables.Count == 0)
            {
                ConsoleUi.ShowWarning("La base de datos no contiene tablas disponibles para este usuario.");
                ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra base de datos.");
                continue;
            }

            var changeDatabase = false;
            while (!changeDatabase)
            {
                ConsoleUi.ShowNamedItems($"Tablas de {database}", tables, "#", allowBack: true);
                var table = ConsoleUi.PromptNamedItem(
                    tables,
                    "Selecciona una tabla por índice o nombre:",
                    allowBack: true);
                if (table is null)
                {
                    changeDatabase = true;
                    continue;
                }

                IReadOnlyList<ColumnInfo> columns;
                try
                {
                    columns = await ConsoleUi.ShowStatusAsync(
                        $"[grey]Consultando columnas de {MarkupEscape(table)}...[/]",
                        () => server.GetColumnsAsync(table, cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ConsoleUi.ShowError(exception);
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                var primaryKeyColumns = columns
                    .Where(column => column.IsPrimaryKey)
                    .OrderBy(column => column.PrimaryKeyPosition)
                    .ToArray();

                if (primaryKeyColumns.Length == 0)
                {
                    ConsoleUi.ShowWarning("La tabla no tiene una clave primaria.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                if (columns.All(column => column.IsPrimaryKey))
                {
                    ConsoleUi.ShowWarning("La tabla no contiene una columna disponible para el documento.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                var changeTable = false;
                while (!changeTable)
                {
                    ConsoleUi.ShowColumns(database, table, columns);
                    var documentColumn = ConsoleUi.PromptColumn(columns);
                    if (documentColumn is null)
                    {
                        changeTable = true;
                        continue;
                    }

                    PrimaryKeyBounds bounds;
                    try
                    {
                        bounds = await ConsoleUi.ShowStatusAsync(
                            "[grey]Consultando primera y última PK...[/]",
                            () => server.GetPrimaryKeyBoundsAsync(
                                table,
                                primaryKeyColumns,
                                cancellationToken));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        ConsoleUi.ShowError(exception);
                        ConsoleUi.WaitForRetry("Presiona Enter para volver a seleccionar la columna.");
                        continue;
                    }

                    var changeColumn = false;
                    while (!changeColumn)
                    {
                        ConsoleUi.ShowRowSelection(table, primaryKeyColumns, documentColumn, bounds);
                        var input = ConsoleUi.PromptRowSelection();
                        if (input is null)
                        {
                            changeColumn = true;
                            continue;
                        }

                        if (!PrimaryKeySelectionParser.TryParse(
                                input,
                                primaryKeyColumns.Length,
                                out var selection,
                                out var syntaxError))
                        {
                            ConsoleUi.ShowWarning(
                                syntaxError ?? "No se pudo interpretar la selección ingresada.");
                            continue;
                        }

                        ProcessingWorkspace? workspace = null;
                        try
                        {
                            workspace = ProcessingWorkspace.Create(ProcessingRoot);
                            var run = new ProcessingRunControl
                            {
                                ProcessingId = workspace.ProcessingId,
                                CreatedAt = DateTimeOffset.Now,
                                Server = server.ServerHost,
                                Database = database,
                                Table = table,
                                DocumentColumn = documentColumn.Name,
                                PrimaryKeyColumns = primaryKeyColumns.Select(column => column.Name).ToArray(),
                                Status = "creating"
                            };
                            await AtomicJsonFile.WriteAsync(workspace.RunPath, run, cancellationToken);
                            _logger.Info(
                                "CONSOLE",
                                "processing.created",
                                "Ejecución creada.",
                                run.ProcessingId,
                                server: server.ServerHost);

                            var export = await ConsoleUi.ShowStatusAsync(
                                "[grey]Ejecutando selección y creando manifiesto...[/]",
                                () => server.ExportSelectionManifestAsync(
                                    table,
                                    primaryKeyColumns,
                                    documentColumn,
                                    selection,
                                    workspace.SelectionPath,
                                    cancellationToken));

                            if (export is null)
                            {
                                ConsoleUi.ShowWarning("La consulta no devolvió filas. Ingresa otra selección.");
                                TryDeleteDirectory(workspace.RootDirectory);
                                continue;
                            }

                            _logger.Info(
                                "SQL",
                                "selection.completed",
                                $"Selección congelada con {export.RowCount} fila(s).",
                                run.ProcessingId,
                                server: server.ServerHost);
                            run.Total = export.RowCount;
                            run.Status = "processing";
                            run.StartedAt = DateTimeOffset.Now;
                            await AtomicJsonFile.WriteAsync(workspace.RunPath, run, cancellationToken);

                            ConsoleUi.ShowHeader("Procesamiento en segundo plano");
                            ConsoleUi.ShowWarning(
                                "El procesamiento continúa en un worker independiente. " +
                                "Esta terminal muestra su log en vivo; Ctrl+C cancela también el worker.");

                            var workerExitCode = await BackgroundProcessingLauncher.StartAndFollowAsync(
                                workspace,
                                credentials.Host,
                                credentials.User,
                                credentials.Password,
                                _logger,
                                cancellationToken);

                            var finalRun = await AtomicJsonFile.ReadAsync<ProcessingRunControl>(
                                               workspace.RunPath,
                                               cancellationToken) ?? run;
                            if (workerExitCode != 0 && !string.IsNullOrWhiteSpace(finalRun.LastError))
                            {
                                ConsoleUi.ShowWarning($"El worker terminó con error: {finalRun.LastError}");
                            }
                            _logger.Info(
                                "CONSOLE",
                                finalRun.Status == "completed" ? "processing.completed" : "processing.pending",
                                finalRun.Status == "completed"
                                    ? "Ejecución finalizada."
                                    : "Ejecución conservada con trabajo pendiente.",
                                finalRun.ProcessingId);
                            ConsoleUi.ShowProcessingSummary(workspace.RootDirectory, finalRun);
                            ConsoleUi.WaitForRetry("Presiona Enter para volver al menú principal.");
                            return;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            _logger.Error(
                                "CONSOLE",
                                "processing.failed",
                                "La ejecución actual no pudo continuar.",
                                exception,
                                workspace?.ProcessingId,
                                server: server.ServerHost);

                            if (workspace is not null && !File.Exists(workspace.SelectionPath))
                            {
                                TryDeleteDirectory(workspace.RootDirectory);
                            }

                            ConsoleUi.ShowError(exception);
                            ConsoleUi.WaitForRetry("Presiona Enter para volver a la selección de filas.");
                        }
                    }
                }
            }
        }
    }

    private async Task RunRecoveryAsync(CancellationToken cancellationToken)
    {
        var runs = await ProcessingRecovery.FindIncompleteRunsAsync(ProcessingRoot, cancellationToken);
        if (runs.Count == 0)
        {
            ConsoleUi.ShowHeader("Recuperar procesamiento");
            ConsoleUi.ShowWarning("No existen ejecuciones recuperables.");
            ConsoleUi.WaitForRetry("Presiona Enter para volver al menú principal.");
            return;
        }

        var selected = ConsoleUi.PromptRecoveryRun(runs);
        if (selected is null)
        {
            return;
        }

        var run = selected.Run;
        var workspace = ProcessingWorkspace.Open(run.ProcessingId, selected.Directory);

        // Si la terminal original se cerró, el worker puede seguir vivo. En ese
        // caso solo nos adjuntamos a su log; jamás iniciamos un segundo worker.
        if (BackgroundProcessingLauncher.IsWorkerRunning(workspace))
        {
            ConsoleUi.ShowHeader("Procesamiento en segundo plano");
            ConsoleUi.ShowWarning(
                "La ejecución ya tiene un worker activo. Se mostrará su log actual.");
            await BackgroundProcessingLauncher.FollowExistingAsync(
                workspace,
                _logger,
                cancellationToken);

            var attachedRun = await AtomicJsonFile.ReadAsync<ProcessingRunControl>(
                                  workspace.RunPath,
                                  cancellationToken) ?? run;
            ConsoleUi.ShowProcessingSummary(workspace.RootDirectory, attachedRun);
            ConsoleUi.WaitForRetry("Presiona Enter para volver al menú principal.");
            return;
        }

        while (true)
        {
            var credentials = ConsoleUi.PromptConnection(run.Server);

            try
            {
                _logger.Info(
                    "CONSOLE",
                    "processing.recovery_started",
                    "Se inicia un worker para continuar la ejecución recuperada.",
                    run.ProcessingId,
                    server: run.Server);

                ConsoleUi.ShowHeader("Procesamiento recuperado");
                ConsoleUi.ShowWarning(
                    "El worker continuará en segundo plano. " +
                    "Esta terminal solo muestra el log; Ctrl+C cancela el procesamiento.");

                var workerExitCode = await BackgroundProcessingLauncher.StartAndFollowAsync(
                    workspace,
                    credentials.Host,
                    credentials.User,
                    credentials.Password,
                    _logger,
                    cancellationToken);

                var finalRun = await AtomicJsonFile.ReadAsync<ProcessingRunControl>(
                                   workspace.RunPath,
                                   cancellationToken) ?? run;

                if (workerExitCode != 0)
                {
                    var detail = finalRun.LastError ?? "El worker terminó sin completar la ejecución.";
                    _logger.Error(
                        "CONSOLE",
                        "processing.recovery_failed",
                        detail,
                        processingId: run.ProcessingId,
                        server: run.Server);
                    ConsoleUi.ShowWarning(detail);
                    ConsoleUi.WaitForRetry("Presiona Enter para volver a solicitar las credenciales.");
                    continue;
                }

                _logger.Info(
                    "CONSOLE",
                    finalRun.Status == "completed" ? "processing.completed" : "processing.pending",
                    finalRun.Status == "completed"
                        ? "Ejecución recuperada finalizada."
                        : "Ejecución recuperada conserva trabajo pendiente.",
                    finalRun.ProcessingId);
                ConsoleUi.ShowProcessingSummary(workspace.RootDirectory, finalRun);
                ConsoleUi.WaitForRetry("Presiona Enter para volver al menú principal.");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Error(
                    "CONSOLE",
                    "processing.recovery_failed",
                    "La recuperación no pudo continuar.",
                    exception,
                    run.ProcessingId,
                    server: run.Server);
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry("Presiona Enter para volver a solicitar las credenciales.");
            }
        }
    }

    private static async Task<(
        SqlServer Server,
        IReadOnlyList<string> Databases,
        (string Host, string User, string Password) Credentials)> ConnectAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var credentials = ConsoleUi.PromptConnection();
            var server = new SqlServer(credentials.Host, credentials.User, credentials.Password);

            try
            {
                var databases = await ConsoleUi.ShowStatusAsync(
                    "[grey]Conectando al servidor SQL Server...[/]",
                    () => server.GetDatabasesAsync(cancellationToken));

                if (databases.Count == 0)
                {
                    ConsoleUi.ShowWarning(
                        "La conexión fue aceptada, pero el usuario no tiene bases de datos visibles.");
                    ConsoleUi.WaitForRetry();
                    continue;
                }

                return (server, databases, credentials);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry();
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Un directorio vacío/incompleto no debe ocultar el error SQL original.
        }
    }

    private static string MarkupEscape(string value) =>
        value.Replace("[", "[[", StringComparison.Ordinal)
             .Replace("]", "]]", StringComparison.Ordinal);
}
