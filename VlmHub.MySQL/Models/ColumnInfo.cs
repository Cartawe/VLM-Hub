namespace VlmHub.MySQL.Models;

/// <summary>
/// Describe una columna de una tabla MySQL.
/// </summary>
public sealed class ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required string ColumnType { get; init; }
    public int Position { get; init; }
    public bool IsNullable { get; init; }
    public int? PrimaryKeyPosition { get; init; }

    /// <summary>
    /// Se calcula a partir de la posición de la PK para no almacenar información duplicada.
    /// </summary>
    public bool IsPrimaryKey => PrimaryKeyPosition.HasValue;
}
