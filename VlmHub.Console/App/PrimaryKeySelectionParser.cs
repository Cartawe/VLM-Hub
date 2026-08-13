using VlmHub.Sql.Models;

namespace VlmHub.Console.App;

/// <summary>
/// Interpreta únicamente la estructura necesaria para construir la consulta SQL.
/// La existencia y compatibilidad de los valores con los tipos de la PK se dejan
/// deliberadamente a SQL Server.
/// </summary>
internal static class PrimaryKeySelectionParser
{
    public static bool TryParse(
        string input,
        int primaryKeyColumnCount,
        out IReadOnlyList<PrimaryKeySelectionPart> selection,
        out string? error)
    {
        selection = Array.Empty<PrimaryKeySelectionPart>();
        error = null;

        var rawParts = input.Split(';', StringSplitOptions.TrimEntries);

        if (rawParts.Length == 0 || rawParts.Any(string.IsNullOrWhiteSpace))
        {
            error = "Ingresa PK o rangos separados por punto y coma.";
            return false;
        }

        var parts = new List<PrimaryKeySelectionPart>(rawParts.Length);

        foreach (var rawPart in rawParts)
        {
            if (primaryKeyColumnCount == 1)
            {
                if (!TryParseSimplePart(rawPart, out var part))
                {
                    error = $"No se puede interpretar '{rawPart}'. Usa, por ejemplo: 1-20; 22.";
                    return false;
                }

                parts.Add(part);
                continue;
            }

            if (!TryParseCompositePart(rawPart, primaryKeyColumnCount, out var compositePart, out error))
            {
                return false;
            }

            parts.Add(compositePart);
        }

        selection = parts;
        return true;
    }

    private static bool TryParseSimplePart(
        string rawPart,
        out PrimaryKeySelectionPart part)
    {
        var firstDash = rawPart.IndexOf('-');
        var lastDash = rawPart.LastIndexOf('-');

        if (firstDash > 0 &&
            firstDash == lastDash &&
            firstDash < rawPart.Length - 1)
        {
            var start = rawPart[..firstDash].Trim();
            var end = rawPart[(firstDash + 1)..].Trim();

            part = new PrimaryKeySelectionPart(
                new PrimaryKeyValue([start]),
                new PrimaryKeyValue([end]));
            return true;
        }

        var value = rawPart.Trim();
        if (value.Length == 0)
        {
            part = null!;
            return false;
        }

        part = new PrimaryKeySelectionPart(new PrimaryKeyValue([value]));
        return true;
    }

    private static bool TryParseCompositePart(
        string rawPart,
        int primaryKeyColumnCount,
        out PrimaryKeySelectionPart part,
        out string? error)
    {
        part = null!;
        error = null;

        var rangeSeparator = FindCompositeRangeSeparator(rawPart);
        var startText = rangeSeparator < 0
            ? rawPart.Trim()
            : rawPart[..rangeSeparator].Trim();
        var endText = rangeSeparator < 0
            ? null
            : rawPart[(rangeSeparator + 1)..].Trim();

        if (!TryParseTuple(startText, primaryKeyColumnCount, out var start))
        {
            error = $"La PK compuesta '{startText}' debe tener {primaryKeyColumnCount} valores entre paréntesis.";
            return false;
        }

        if (endText is null)
        {
            part = new PrimaryKeySelectionPart(start);
            return true;
        }

        if (!TryParseTuple(endText, primaryKeyColumnCount, out var end))
        {
            error = $"La PK compuesta '{endText}' debe tener {primaryKeyColumnCount} valores entre paréntesis.";
            return false;
        }

        // El formato acordado para rangos compuestos solo hace variar la última
        // columna de la PK. Esta es una regla de sintaxis, no una validación de datos.
        for (var index = 0; index < primaryKeyColumnCount - 1; index++)
        {
            if (!string.Equals(start.Values[index], end.Values[index], StringComparison.Ordinal))
            {
                error = "En un rango de PK compuesta solo puede variar el último componente.";
                return false;
            }
        }

        part = new PrimaryKeySelectionPart(start, end);
        return true;
    }

    private static int FindCompositeRangeSeparator(string value)
    {
        var depth = 0;

        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case '-' when depth == 0:
                    return index;
            }
        }

        return -1;
    }

    private static bool TryParseTuple(
        string value,
        int expectedCount,
        out PrimaryKeyValue tuple)
    {
        tuple = null!;

        if (value.Length < 2 || value[0] != '(' || value[^1] != ')')
        {
            return false;
        }

        var values = value[1..^1]
            .Split(',', StringSplitOptions.TrimEntries);

        if (values.Length != expectedCount || values.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        tuple = new PrimaryKeyValue(values);
        return true;
    }
}
