using VlmHub.Balancer.Logging;

namespace VlmHub.Balancer.Configuration;

/// <summary>
/// Parámetros operacionales del balanceador. Los límites controlan fallos reales,
/// no el tiempo normal que una unidad puede permanecer esperando capacidad.
/// </summary>
public sealed class ProcessingOptions
{
    /// <summary>Frecuencia de consulta de /api/status.</summary>
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Frecuencia con la que se vuelve a leer servidores.json. Permite agregar
    /// o retirar VLM Servers sin reiniciar el balanceador.
    /// </summary>
    public TimeSpan ServerConfigRefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ModelListRefreshInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan StatusRequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ProcessingRequestTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ModelCommandTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan StuckStateTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ServerCooldown { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ModelFailureCooldown { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SchedulerDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Límite global de fallos de una unidad. Suma finish_reason=error y fallos
    /// técnicos de infraestructura/modelo. stop y length no consumen este límite.
    /// </summary>
    public int MaxGlobalFailuresPerUnit { get; init; } = 12;

    /// <summary>
    /// Fallos técnicos máximos por unidad. Se mantiene separado del límite
    /// global para diagnosticar infraestructura defectuosa.
    /// </summary>
    public int MaxInfrastructureFailuresPerUnit { get; init; } = 10;

    /// <summary>
    /// Máximo aproximado de documentos preparados/residentes simultáneamente.
    /// El coordinador usa ventanas y prefetch acotado para no cargar miles de
    /// documentos y páginas en memoria al mismo tiempo.
    /// </summary>
    public int MaxDocumentsInMemory { get; init; } = 32;

    /// <summary>
    /// Si es true, la siguiente ventana se prepara mientras la actual se procesa.
    /// El tamaño de cada ventana se reduce para respetar MaxDocumentsInMemory.
    /// </summary>
    public bool PrefetchNextDocumentWindow { get; init; } = true;

    /// <summary>
    /// Cantidad máxima de documentos que pueden estar en preparación al mismo
    /// tiempo dentro de una ventana. PDFtoImage protege internamente PDFium.
    /// </summary>
    public int MaxParallelDocumentPreparation { get; init; } = 2;

    /// <summary>
    /// Mantener el detalle por página dentro de BatchResult incrementa memoria
    /// proporcionalmente al número total de páginas. Por defecto se conserva
    /// solo el resumen por documento; el detalle operacional queda en el .log.
    /// </summary>
    public bool RetainUnitSummariesInBatchResult { get; init; } = false;

    public int PdfDpi { get; init; } = 200;

    public string LogDirectory { get; init; } =
        Path.Combine(Environment.CurrentDirectory, "logs");

    /// <summary>
    /// Logger compartido por el ejecutable. Si no se entrega, Balancer crea un
    /// logger propio únicamente para mantener compatibilidad como librería.
    /// </summary>
    public VlmHubLogger? SharedLogger { get; init; }

    public bool KeepTemporaryFilesOnFailure { get; init; } = true;
}
