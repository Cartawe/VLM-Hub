namespace VlmHub.Sql.Models;

/// <summary>
/// Resultado de ejecutar la selección y escribir sus filas en un JSON temporal.
/// </summary>
public sealed record SelectionExportResult(
    string FilePath,
    long RowCount);
