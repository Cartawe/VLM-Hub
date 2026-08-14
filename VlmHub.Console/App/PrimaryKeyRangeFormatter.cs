using System.Globalization;
using System.Runtime.CompilerServices;
using VlmHub.Sql.Models;

namespace VlmHub.Console.App;

/// <summary>
/// Convierte un stream ordenado de PK en el mismo formato
/// que acepta PrimaryKeySelectionParser.
///
/// Ejemplos:
///
/// 1,2,3,7,8
///     -> 1-3; 7-8
///
/// (2025,1),(2025,2),(2025,3),(2026,1)
///     -> (2025,1)-(2025,3); (2026,1)
///
/// No materializa todas las PK en memoria.
/// </summary>
internal static class PrimaryKeyRangeFormatter
{
    public static async IAsyncEnumerable<string> GroupAsync(
        IAsyncEnumerable<PrimaryKeyValue> primaryKeys,
        IReadOnlyList<ColumnInfo> primaryKeyColumns,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (primaryKeyColumns.Count == 0)
        {
            yield break;
        }

        var lastColumn =
            primaryKeyColumns[^1];

        var canCreateRanges =
            IsIntegerType(lastColumn.DataType);

        PrimaryKeyValue? rangeStart = null;
        PrimaryKeyValue? previous = null;

        await foreach (var current in
            primaryKeys.WithCancellation(cancellationToken))
        {
            if (rangeStart is null)
            {
                rangeStart = current;
                previous = current;
                continue;
            }

            if (canCreateRanges &&
                previous is not null &&
                AreConsecutive(
                    previous,
                    current,
                    primaryKeyColumns.Count))
            {
                previous = current;
                continue;
            }

            yield return FormatRange(
                rangeStart,
                previous!);

            rangeStart = current;
            previous = current;
        }

        if (rangeStart is not null &&
            previous is not null)
        {
            yield return FormatRange(
                rangeStart,
                previous);
        }
    }

    private static bool AreConsecutive(
        PrimaryKeyValue previous,
        PrimaryKeyValue current,
        int keyCount)
    {
        if (previous.Values.Count != keyCount ||
            current.Values.Count != keyCount)
        {
            return false;
        }

        // En PK compuesta todos los componentes
        // excepto el último deben ser iguales.
        for (var index = 0;
             index < keyCount - 1;
             index++)
        {
            if (!string.Equals(
                    previous.Values[index],
                    current.Values[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!long.TryParse(
                previous.Values[^1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var previousValue))
        {
            return false;
        }

        if (!long.TryParse(
                current.Values[^1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var currentValue))
        {
            return false;
        }

        if (previousValue == long.MaxValue)
        {
            return false;
        }

        return currentValue == previousValue + 1;
    }

    private static string FormatRange(
        PrimaryKeyValue start,
        PrimaryKeyValue end)
    {
        if (start.Values.SequenceEqual(end.Values))
        {
            return start.ToString();
        }

        return $"{start}-{end}";
    }

    private static bool IsIntegerType(string dataType)
    {
        return dataType.Equals(
                   "tinyint",
                   StringComparison.OrdinalIgnoreCase)
            || dataType.Equals(
                   "smallint",
                   StringComparison.OrdinalIgnoreCase)
            || dataType.Equals(
                   "int",
                   StringComparison.OrdinalIgnoreCase)
            || dataType.Equals(
                   "bigint",
                   StringComparison.OrdinalIgnoreCase);
    }
}