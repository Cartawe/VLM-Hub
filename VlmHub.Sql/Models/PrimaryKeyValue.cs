namespace VlmHub.Sql.Models;

/// <summary>
/// Valores que forman una clave primaria, respetando el orden definido por SQL Server.
/// Para una PK simple contiene un valor; para una PK compuesta contiene varios.
/// </summary>
public sealed record PrimaryKeyValue(IReadOnlyList<string> Values)
{
    public override string ToString() =>
        Values.Count == 1
            ? Values[0]
            : $"({string.Join(",", Values)})";
}
