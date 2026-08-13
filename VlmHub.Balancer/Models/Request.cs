namespace VlmHub.Balancer.Models;

/// <summary>
/// Entrada pública del módulo. Puede contener un documento o una colección.
/// </summary>
public sealed class ProcessingRequest
{
    public required IReadOnlyList<string> Documents { get; init; }
    public required IReadOnlyList<string> ModelPriority { get; init; }

    /// <summary>
    /// Cantidad máxima de respuestas finish_reason=error permitidas por unidad.
    /// stop y length son resultados terminales aceptados y no consumen intentos.
    /// </summary>
    public required int MaxAttempts { get; init; }

    /// <summary>
    /// Archivo observado durante todo el lote. Puede modificarse en ejecución
    /// para agregar o retirar VLM Servers.
    /// </summary>
    public required string ServersFile { get; init; }

    public required string OutputDirectory { get; init; }

    public Guid? ProcessingId { get; init; }

    /// <summary>
    /// Notificación incremental de cada despacho terminado. Los errores del
    /// consumidor no deben detener el scheduler del balanceador.
    /// </summary>
    public Func<ProcessingAttemptEvent, CancellationToken, Task>? AttemptCompleted { get; init; }

    /// <summary>
    /// Se invoca apenas todas las unidades de un documento quedan resueltas,
    /// sin esperar a que termine el resto del lote.
    /// </summary>
    public Func<ProcessedDocument, CancellationToken, Task>? DocumentCompleted { get; init; }
}
