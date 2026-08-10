using VlmHub.Console.Views;
using VlmHub.Sql;
using VlmHub.Sql.Models;

namespace VlmHub.Console.App;

/// <summary>
/// Orquesta el flujo principal de VLMHub.
/// ConsoleUi se ocupa únicamente de la presentación y SqlServer de las consultas
/// contra Microsoft SQL Server.
/// </summary>
public sealed class VlmHubConsoleApp
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // 1) Conexión al servidor. Si falla, el usuario puede volver a intentarlo
        // sin cerrar VLMHub.
        var (server, databases) = await ConnectAsync(cancellationToken);

        // 2) Selección de base de datos.
        var (database, tables) = await SelectDatabaseAsync(
            server,
            databases,
            cancellationToken);

        // 3) Selección de tabla.
        var (table, columns) = await SelectTableAsync(
            server,
            database,
            tables,
            cancellationToken);

        // 4) Selección de columna documental. Las columnas PK se muestran,
        // pero ConsoleUi impide seleccionarlas.
        ConsoleUi.ShowColumns(database, table, columns);
        var column = ConsoleUi.PromptColumn(columns);

        ConsoleUi.ShowSuccess(server.ServerHost, database, table, column);
    }

    private static async Task<(SqlServer Server, IReadOnlyList<string> Databases)> ConnectAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var credentials = ConsoleUi.PromptConnection();
            var server = new SqlServer(
                credentials.Host,
                credentials.User,
                credentials.Password);

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

                return (server, databases);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry();
            }
        }
    }

    private static async Task<(string Database, IReadOnlyList<string> Tables)> SelectDatabaseAsync(
        SqlServer server,
        IReadOnlyList<string> databases,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ConsoleUi.ShowNamedItems("Bases de datos disponibles", databases, "#");
            var database = ConsoleUi.PromptNamedItem(
                databases,
                "Selecciona una base de datos por índice o nombre:");

            server.SetDatabase(database);

            try
            {
                var tables = await ConsoleUi.ShowStatusAsync(
                    $"[grey]Consultando tablas de {MarkupEscape(database)}...[/]",
                    () => server.GetTablesAsync(cancellationToken));

                if (tables.Count == 0)
                {
                    ConsoleUi.ShowWarning(
                        "La base de datos no contiene tablas disponibles para este usuario.");
                    ConsoleUi.WaitForRetry(
                        "Presiona Enter para seleccionar otra base de datos.");
                    continue;
                }

                return (database, tables);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry(
                    "Presiona Enter para volver a seleccionar una base de datos.");
            }
        }
    }

    private static async Task<(string Table, IReadOnlyList<ColumnInfo> Columns)> SelectTableAsync(
        SqlServer server,
        string database,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ConsoleUi.ShowNamedItems($"Tablas de {database}", tables, "#");
            var table = ConsoleUi.PromptNamedItem(
                tables,
                "Selecciona una tabla por índice o nombre:");

            try
            {
                var columns = await ConsoleUi.ShowStatusAsync(
                    $"[grey]Consultando columnas de {MarkupEscape(table)}...[/]",
                    () => server.GetColumnsAsync(table, cancellationToken));

                if (columns.Count == 0)
                {
                    ConsoleUi.ShowWarning(
                        "La tabla no existe, no es accesible o no contiene columnas disponibles.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                if (columns.All(column => column.IsPrimaryKey))
                {
                    ConsoleUi.ShowWarning(
                        "La tabla solo contiene columnas que forman parte de la clave primaria.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                return (table, columns);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);
                ConsoleUi.WaitForRetry(
                    "Presiona Enter para volver a seleccionar una tabla.");
            }
        }
    }

    // Los mensajes de Status aceptan markup de Spectre.Console. Los nombres de
    // objetos provienen de SQL Server y se escapan antes de mostrarlos.
    private static string MarkupEscape(string value) =>
        value.Replace("[", "[[", StringComparison.Ordinal)
             .Replace("]", "]]", StringComparison.Ordinal);
}
