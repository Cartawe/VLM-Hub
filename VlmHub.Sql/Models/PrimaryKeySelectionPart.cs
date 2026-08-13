namespace VlmHub.Sql.Models;

/// <summary>
/// Segmento de la selección de filas. End es null para una PK individual.
/// En una PK compuesta, los rangos mantienen fijos todos los componentes salvo el último.
/// </summary>
public sealed record PrimaryKeySelectionPart(PrimaryKeyValue Start, PrimaryKeyValue? End = null)
{
    public bool IsRange => End is not null;

    public override string ToString() =>
        IsRange ? $"{Start}-{End}" : Start.ToString();
}
