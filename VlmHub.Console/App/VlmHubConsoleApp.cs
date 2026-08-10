using VlmHub.Console.Views;
using VlmHub.MySQL;
using VlmHub.MySQL.Models;

namespace VlmHub.Console.App;

/// <summary>
/// Orquesta el flujo de VLMHub. Aquí se decide qué paso viene después,
/// mientras ConsoleUi presenta datos y MySqlServer se comunica con MySQL.
/// </summary>
public sealed class VlmHubConsoleApp
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // 1) La conexión se repite hasta que MySQL acepte las credenciales y
        // sea posible obtener las bases de datos visibles para ese usuario.
        var (server, databases) = await ConnectAsync(cancellationToken);

        // 2) Elegir base de datos y obtener sus tablas.
        var (database, tables) = await SelectDatabaseAsync(
            server,
            databases,
            cancellationToken);

        // 3) Elegir tabla y obtener sus columnas.
        var (table, columns) = await SelectTableAsync(
            server,
            database,
            tables,
            cancellationToken);

        // 4) Mostrar columnas. Las PK son visibles pero no seleccionables.
        ConsoleUi.ShowColumns(database, table, columns);
        var column = ConsoleUi.PromptColumn(columns);

        ConsoleUi.ShowSuccess(server.ServerHost, database, table, column);
    }

    private static async Task<(MySqlServer Server, IReadOnlyList<string> Databases)> ConnectAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var credentials = ConsoleUi.PromptConnection();
            var server = new MySqlServer(
                credentials.Host,
                credentials.User,
                credentials.Password);
            
            credentials =

            try
            {
                var databases = await ConsoleUi.ShowStatusAsync(
                    "[grey]Conectando al servidor MySQL...[/]",
                    () => server.GetDatabasesAsync(cancellationToken));

                if (databases.Count == 0)
                {
                    ConsoleUi.ShowWarning("La conexión fue aceptada, pero el usuario no tiene bases de datos visibles.");
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
        MySqlServer server,
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
                    ConsoleUi.ShowWarning("La base de datos no contiene tablas disponibles para este usuario.");

                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra base de datos.");
                    continue;
                }

                return (database, tables);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);

                // Un fallo aquí no termina la sesión. Se vuelve al listado de BD
                // para que el usuario pueda reintentar la misma u otra.
                ConsoleUi.WaitForRetry("Presiona Enter para volver a seleccionar una base de datos.");
            }
        }
    }

    private static async Task<(string Table, IReadOnlyList<ColumnInfo> Columns)> SelectTableAsync(
        MySqlServer server,
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
                    ConsoleUi.ShowWarning("La tabla no contiene columnas disponibles.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                if (columns.All(column => column.IsPrimaryKey))
                {
                    ConsoleUi.ShowWarning("La tabla solo contiene columnas que forman parte de la clave primaria.");
                    ConsoleUi.WaitForRetry("Presiona Enter para seleccionar otra tabla.");
                    continue;
                }

                return (table, columns);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ConsoleUi.ShowError(exception);

                // Igual que con la BD: el error no derriba VLMHub.
                ConsoleUi.WaitForRetry("Presiona Enter para volver a seleccionar una tabla.");
            }
        }
    }

    // Los mensajes de Status aceptan markup de Spectre.Console. Para nombres
    // externos usamos un escape mínimo aquí sin acoplar toda la clase a la UI.
    private static string MarkupEscape(string value) =>
        value.Replace("[", "[[", StringComparison.Ordinal)
             .Replace("]", "]]", StringComparison.Ordinal);
}
