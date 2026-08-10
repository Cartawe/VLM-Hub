using MySqlConnector;
using Spectre.Console;
using VlmHub.MySQL.Models;

namespace VlmHub.Console.Views;

/// <summary>
/// Toda la presentación de la consola vive aquí. De esta forma el flujo de la
/// aplicación no queda acoplado a colores, paneles o tablas de Spectre.Console.
/// </summary>
public static class ConsoleUi
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

    public static (string Host, string User, string Password) PromptConnection()
    {
        ShowHeader("Conexión a MySQL");

        var panel = new Panel(
            new Markup(
                "[bold white]Conecta VLMHub a un servidor MySQL[/]\n\n" +
                "[grey]Las credenciales se utilizan únicamente para abrir la conexión actual.[/]"))
        {
            Header = new PanelHeader("[grey] SERVIDOR [/]") ,
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        var host = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Servidor / IP:[/] ")
                .PromptStyle("white"));

        var user = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Usuario:[/] ")
                .PromptStyle("white"));

        var password = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Contraseña:[/] ")
                .PromptStyle("white")
                .Secret());

        return (host, user, password);
    }

    public static void ShowNamedItems(
        string title,
        IReadOnlyList<string> items,
        string indexHeader)
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
    }

    public static string PromptNamedItem(
        IReadOnlyList<string> items,
        string prompt)
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>($"\n[cyan]{Markup.Escape(prompt)}[/] ").Trim();

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
        AnsiConsole.MarkupLine("\n[grey]Las columnas marcadas como PK se muestran solo como información y no pueden seleccionarse.[/]");
    }

    public static ColumnInfo PromptColumn(IReadOnlyList<ColumnInfo> columns)
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>(
                "\n[cyan]Selecciona la columna por índice o nombre:[/] ").Trim();

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
                ShowWarning("La clave primaria identifica los registros y no puede seleccionarse como columna documental.");
                continue;
            }

            return selected;
        }
    }

    public static Task<T> ShowStatusAsync<T>(
        string message,
        Func<Task<T>> operation)
    {
        return AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, _ => operation());
    }

    public static void WaitForRetry(string message = "Presiona Enter para intentarlo nuevamente.")
    {
        AnsiConsole.MarkupLine($"\n[grey]{Markup.Escape(message)}[/]");
        System.Console.ReadLine();
    }

    public static void ShowError(Exception exception)
    {
        var message = exception switch
        {
            MySqlException { Number: 1045 } =>
                "MySQL rechazó el usuario o la contraseña.",

            MySqlException { Number: 1049 } =>
                "La base de datos seleccionada ya no está disponible.",

            MySqlException { Number: 1146 } =>
                "La tabla seleccionada ya no existe o no está disponible.",

            MySqlException mysqlException =>
                $"MySQL respondió con un error: {mysqlException.Message}",

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
        ColumnInfo column)
    {
        ShowHeader("Selección completada");

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[grey]Servidor[/]", Markup.Escape(host));
        grid.AddRow("[grey]Base de datos[/]", Markup.Escape(database));
        grid.AddRow("[grey]Tabla[/]", Markup.Escape(table));
        grid.AddRow("[grey]Columna[/]", Markup.Escape(column.Name));
        grid.AddRow("[grey]Tipo[/]", Markup.Escape(column.ColumnType));

        AnsiConsole.Write(
            new Panel(grid)
            {
                Header = new PanelHeader("[green] LISTO [/]") ,
                Border = BoxBorder.Rounded,
                Padding = new Padding(2, 1)
            });

        AnsiConsole.MarkupLine("\n[green]✓ VLMHub ya tiene definido el origen que se procesará.[/]");
    }
}
