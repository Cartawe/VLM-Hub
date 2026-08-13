using Microsoft.Data.SqlClient;
using Spectre.Console;
using VlmHub.Sql.Models;
using VlmHub.Console.Processing;

namespace VlmHub.Console.Views;

/// <summary>
/// Centraliza toda la presentación de VLMHub en consola.
/// No contiene consultas SQL ni decide el orden del flujo de la aplicación.
/// </summary>
internal static class ConsoleUi
{
    public static void ShowHeader(string? subtitle = null)
    {
        AnsiConsole.Clear();

        AnsiConsole.Write(
            new FigletText("VLMHub")
                .Centered()
                .Color(Color.White));

        AnsiConsole.Write(
            new Rule("[grey]Visual Language Model Hub[/]")
                .RuleStyle("grey")
                .Centered());

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            AnsiConsole.MarkupLine($"\n[bold cyan]{Markup.Escape(subtitle)}[/]\n");
        }
    }

    public static (string Host, string User, string Password) PromptConnection(string? fixedHost = null)
    {
        ShowHeader("Conexión a SQL Server");

        var panel = new Panel(
            new Markup(
                "[bold white]Conecta VLMHub a un servidor SQL Server[/]\n\n" +
                "[grey]Una vez iniciada la sesión, el flujo no vuelve a solicitar las credenciales.[/]"))
        {
            Header = new PanelHeader("[grey] SERVIDOR [/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        var host = fixedHost ?? AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Servidor / IP:[/] ")
                .PromptStyle("white"));

        if (fixedHost is not null)
        {
            AnsiConsole.MarkupLine($"[cyan]Servidor / IP:[/] {Markup.Escape(fixedHost)}");
        }

        var user = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Usuario:[/] ")
                .PromptStyle("white"));

        var password = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Contraseña:[/] ")
                .PromptStyle("white")
                .Secret());

        return (host, user, password);
    }

    public static int PromptStartOption()
    {
        ShowHeader();
        AnsiConsole.MarkupLine("[cyan]1.[/] Nuevo procesamiento");
        AnsiConsole.MarkupLine("[cyan]2.[/] Recuperar procesamiento");
        AnsiConsole.MarkupLine("[grey]0.[/] Salir");

        while (true)
        {
            var input = AnsiConsole.Ask<string>("\n[cyan]Selecciona una opción:[/] ").Trim();
            if (int.TryParse(input, out var option) && option is >= 0 and <= 2)
            {
                return option;
            }
            ShowWarning("Opción no disponible.");
        }
    }

    public static ProcessingRunSummary? PromptRecoveryRun(
        IReadOnlyList<ProcessingRunSummary> runs)
    {
        ShowHeader("Recuperar procesamiento");
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[grey]#[/]").Centered())
            .AddColumn("[grey]Fecha[/]")
            .AddColumn("[grey]Servidor[/]")
            .AddColumn("[grey]Base[/]")
            .AddColumn("[grey]Tabla[/]")
            .AddColumn(new TableColumn("[grey]Total[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Completados[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Pendientes[/]").RightAligned());

        for (var index = 0; index < runs.Count; index++)
        {
            var item = runs[index];
            table.AddRow(
                $"[cyan]{index + 1}[/]",
                Markup.Escape(item.Run.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
                Markup.Escape(item.Run.Server),
                Markup.Escape(item.Run.Database),
                Markup.Escape(item.Run.Table),
                item.Run.Total.ToString(),
                item.Completed.ToString(),
                item.Pending.ToString());
        }

        AnsiConsole.Write(table);
        ShowBackOption();

        while (true)
        {
            var input = AnsiConsole.Ask<string>("\n[cyan]Selecciona la ejecución:[/] ").Trim();
            if (string.Equals(input, "/volver", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (int.TryParse(input, out var selected) && selected >= 1 && selected <= runs.Count)
            {
                return runs[selected - 1];
            }

            ShowWarning("No se encontró esa ejecución.");
        }
    }

    public static void ShowProcessingSummary(
        string processingDirectory,
        ProcessingRunControl run)
    {
        ShowHeader(run.Status == "completed" ? "Procesamiento completado" : "Procesamiento conservado");
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]ProcessingId[/]", Markup.Escape(run.ProcessingId.ToString()));
        grid.AddRow("[grey]Estado[/]", Markup.Escape(run.Status));
        grid.AddRow("[grey]Total[/]", run.Total.ToString());
        grid.AddRow("[grey]Éxito[/]", run.Success?.ToString() ?? "-");
        grid.AddRow("[grey]Fallidos[/]", run.Failed?.ToString() ?? "-");
        grid.AddRow("[grey]Carpeta[/]", Markup.Escape(processingDirectory));

        AnsiConsole.Write(new Panel(grid)
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        });

        if (run.Status == "completed")
        {
            AnsiConsole.MarkupLine("\n[green]✓ Todos los documentos quedaron registrados permanentemente.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[yellow]⚠ La ejecución conserva trabajo pendiente y puede recuperarse desde el menú inicial.[/]");
        }
    }

    public static void ShowNamedItems(
        string title,
        IReadOnlyList<string> items,
        string indexHeader,
        bool allowBack = false)
    {
        ShowHeader(title);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn($"[grey]{Markup.Escape(indexHeader)}[/]").Centered())
            .AddColumn(new TableColumn("[grey]Nombre[/]"));

        for (var index = 0; index < items.Count; index++)
        {
            table.AddRow(
                $"[cyan]{index + 1}[/]",
                Markup.Escape(items[index]));
        }

        AnsiConsole.Write(table);

        if (allowBack)
        {
            ShowBackOption();
        }
    }

    public static string? PromptNamedItem(
        IReadOnlyList<string> items,
        string prompt,
        bool allowBack = false)
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>(
                $"\n[cyan]{Markup.Escape(prompt)}[/] ").Trim();

            if (allowBack && string.Equals(input, "/volver", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (int.TryParse(input, out var selectedIndex) &&
                selectedIndex >= 1 &&
                selectedIndex <= items.Count)
            {
                return items[selectedIndex - 1];
            }

            var selectedByName = items.FirstOrDefault(
                item => string.Equals(item, input, StringComparison.OrdinalIgnoreCase));

            if (selectedByName is not null)
            {
                return selectedByName;
            }

            ShowWarning("No se encontró ese índice o nombre. Inténtalo nuevamente.");
        }
    }

    public static void ShowColumns(
        string database,
        string tableName,
        IReadOnlyList<ColumnInfo> columns)
    {
        ShowHeader($"{database}  ›  {tableName}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("[grey]#[/]").Centered())
            .AddColumn(new TableColumn("[grey]Columna[/]"))
            .AddColumn(new TableColumn("[grey]Tipo[/]"))
            .AddColumn(new TableColumn("[grey]NULL[/]").Centered())
            .AddColumn(new TableColumn("[grey]Clave[/]").Centered());

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            var key = column.IsPrimaryKey
                ? $"[yellow]PK #{column.PrimaryKeyPosition}[/]"
                : "[grey]-[/]";
            var number = column.IsPrimaryKey
                ? $"[grey]{index + 1}[/]"
                : $"[cyan]{index + 1}[/]";
            var name = column.IsPrimaryKey
                ? $"[grey]{Markup.Escape(column.Name)}[/]"
                : Markup.Escape(column.Name);

            table.AddRow(
                number,
                name,
                Markup.Escape(column.ColumnType),
                column.IsNullable ? "[grey]Sí[/]" : "No",
                key);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "\n[grey]Las columnas PK identifican las filas y no pueden seleccionarse como columna documental.[/]");
        ShowBackOption();
    }

    public static ColumnInfo? PromptColumn(IReadOnlyList<ColumnInfo> columns)
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>(
                "\n[cyan]Selecciona la columna por índice o nombre:[/] ").Trim();

            if (string.Equals(input, "/volver", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ColumnInfo? selected = null;

            if (int.TryParse(input, out var selectedIndex) &&
                selectedIndex >= 1 &&
                selectedIndex <= columns.Count)
            {
                selected = columns[selectedIndex - 1];
            }
            else
            {
                selected = columns.FirstOrDefault(
                    column => string.Equals(
                        column.Name,
                        input,
                        StringComparison.OrdinalIgnoreCase));
            }

            if (selected is null)
            {
                ShowWarning("No se encontró esa columna. Inténtalo nuevamente.");
                continue;
            }

            if (selected.IsPrimaryKey)
            {
                ShowWarning("Selecciona una columna que contenga el documento.");
                continue;
            }

            return selected;
        }
    }

    public static void ShowRowSelection(
        string tableName,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        ColumnInfo documentColumn,
        PrimaryKeyBounds bounds)
    {
        ShowHeader($"Selección de filas  ›  {tableName}");

        var keyDescription = string.Join(
            ", ",
            primaryKeyColumns.Select(column =>
                $"{column.Name} [{column.ColumnType}]"));

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]Clave primaria[/]", Markup.Escape(keyDescription));
        grid.AddRow("[grey]Primera PK[/]", Markup.Escape(bounds.First?.ToString() ?? "Sin filas"));
        grid.AddRow("[grey]Última PK[/]", Markup.Escape(bounds.Last?.ToString() ?? "Sin filas"));
        grid.AddRow("[grey]Columna documental[/]", Markup.Escape(documentColumn.Name));

        AnsiConsole.Write(
            new Panel(grid)
            {
                Header = new PanelHeader("[grey] ORIGEN [/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(2, 1)
            });

        if (primaryKeyColumns.Count == 1)
        {
            AnsiConsole.MarkupLine(
                "\n[grey]Combina rangos y PK individuales separados por punto y coma.[/]");
            AnsiConsole.MarkupLine("[grey]Ejemplo:[/] [cyan]1-20; 22[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                "\n[grey]Para una PK compuesta usa paréntesis respetando el orden mostrado arriba.[/]");
            AnsiConsole.MarkupLine(
                "[grey]En un rango compuesto solo puede variar el último componente.[/]");
            AnsiConsole.MarkupLine(
                "[grey]Ejemplo:[/] [cyan](2025,1)-(2025,20); (2026,4)[/]");
        }

        ShowBackOption();
    }

    public static string? PromptRowSelection()
    {
        var input = AnsiConsole.Ask<string>(
            "\n[cyan]Selecciona las filas por PK:[/] ").Trim();

        return string.Equals(input, "/volver", StringComparison.OrdinalIgnoreCase)
            ? null
            : input;
    }

    public static Task<T> ShowStatusAsync<T>(
        string message,
        Func<Task<T>> operation)
    {
        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, _ => operation());
    }

    public static void WaitForRetry(
        string message = "Presiona Enter para intentarlo nuevamente.")
    {
        AnsiConsole.MarkupLine($"\n[grey]{Markup.Escape(message)}[/]");
        System.Console.ReadLine();
    }

    public static void ShowError(Exception exception)
    {
        var message = exception switch
        {
            SqlException { Number: 18456 } =>
                "SQL Server rechazó el usuario o la contraseña.",
            SqlException { Number: 4060 } =>
                "No se pudo abrir la base de datos seleccionada. Puede no existir o el usuario no tiene acceso.",
            SqlException { Number: 208 } =>
                "La tabla u objeto solicitado ya no existe o no está disponible.",
            SqlException { Number: 229 } =>
                "El usuario no tiene permisos suficientes para realizar esta consulta.",
            SqlException { Number: -2 } =>
                "SQL Server tardó demasiado en responder y la operación expiró.",
            SqlException sqlException =>
                $"SQL Server respondió con un error ({sqlException.Number}): {sqlException.Message}",
            OperationCanceledException =>
                "La operación fue cancelada.",
            _ => exception.Message
        };

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");
    }

    public static void ShowWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(message)}[/]");
    }

    public static void ShowSuccess(
        string host,
        string database,
        string table,
        ColumnInfo documentColumn,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        string rowSelection,
        SelectionExportResult export)
    {
        ShowHeader("Selección completada");

        var primaryKey = string.Join(
            ", ",
            primaryKeyColumns.Select(column => column.Name));

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]Servidor[/]", Markup.Escape(host));
        grid.AddRow("[grey]Base de datos[/]", Markup.Escape(database));
        grid.AddRow("[grey]Tabla[/]", Markup.Escape(table));
        grid.AddRow("[grey]Columna DOC[/]", Markup.Escape(documentColumn.Name));
        grid.AddRow("[grey]PK[/]", Markup.Escape(primaryKey));
        grid.AddRow("[grey]Selección[/]", Markup.Escape(rowSelection));
        grid.AddRow("[grey]Filas obtenidas[/]", export.RowCount.ToString());
        grid.AddRow("[grey]Manifiesto[/]", Markup.Escape(export.FilePath));

        AnsiConsole.Write(
            new Panel(grid)
            {
                Header = new PanelHeader("[green] LISTO [/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(2, 1)
            });

        AnsiConsole.MarkupLine(
            "\n[green]✓ La selección quedó congelada en su manifiesto de procesamiento.[/]");
    }

    private static void ShowBackOption()
    {
        AnsiConsole.MarkupLine("\n[grey]/volver[/]  Retroceder");
    }
}
