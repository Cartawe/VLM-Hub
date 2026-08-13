namespace VlmHub.Sql.Models;

/// <summary>
/// Primera y última PK según el orden natural de la clave primaria.
/// Ambas son null cuando la tabla no contiene filas.
/// </summary>
public sealed record PrimaryKeyBounds(
    PrimaryKeyValue? First,
    PrimaryKeyValue? Last);
