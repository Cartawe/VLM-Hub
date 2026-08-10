namespace VlmHub.Balancer.Configuration;

/// <summary>
/// Parámetros operacionales del balanceador. No contienen información propia
/// de un documento; controlan tiempos de espera, sondeo y recuperación.
/// </summary>
public sealed class ProcessingOptions
{
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ModelListRefreshInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StatusRequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProcessingRequestTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan StuckStateTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ServerCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ModelFailureCooldown { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SchedulerDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan NoServerAvailableTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Límite técnico independiente de MaxAttempts. Evita un bucle infinito
    /// cuando una unidad solo encuentra fallos de transporte/infraestructura.
    /// </summary>
    public int MaxInfrastructureFailuresPerUnit { get; init; } = 5;

    /// <summary>
    /// Resolución usada para rasterizar las páginas PDF antes de enviarlas a
    /// /api/process/images.
    /// </summary>
    public int PdfDpi { get; init; } = 200;
}
