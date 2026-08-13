using System.Diagnostics;

namespace VlmHub.Balancer.Models;

internal sealed class PreparedDocument : IDisposable
{
    private long _processingStartedTimestamp;
    private long _processingFinishedTimestamp;

    public Guid Id { get; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public required string Type { get; init; }
    public string? PreparationError { get; set; }
    public string? OutputPath { get; set; }
    public string? StateDirectory { get; set; }
    public IReadOnlyList<ProcessingUnit> Units { get; set; } = Array.Empty<ProcessingUnit>();

    // Solo existe mientras el documento está dentro de una ventana activa.
    // Serializa actualizaciones concurrentes de páginas del mismo PDF sin
    // bloquear escrituras de otros documentos.
    public SemaphoreSlim ResultWriteLock { get; } = new(1, 1);

    /// <summary>
    /// Fija una sola vez el comienzo real del procesamiento VLM del documento.
    /// Varias páginas pueden despacharse en paralelo, por lo que la primera
    /// llamada gana de forma atómica.
    /// </summary>
    public void MarkProcessingStarted()
    {
        var timestamp = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _processingStartedTimestamp, timestamp, 0);
    }

    /// <summary>
    /// Fija el final cuando todas las unidades ya están resueltas. Se puede
    /// llamar concurrentemente desde varias páginas sin duplicar el cierre.
    /// </summary>
    public void MarkProcessingCompletedIfResolved()
    {
        if (Interlocked.Read(ref _processingStartedTimestamp) == 0 ||
            Units.Count == 0 ||
            Units.Any(unit => !unit.IsCompleted && !unit.IsTerminalFailure))
        {
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _processingFinishedTimestamp, timestamp, 0);
    }

    /// <summary>
    /// Tiempo monotónico entre la primera ejecución VLM y la resolución de la
    /// última unidad. Si aún no se ha fijado el final usa el instante actual.
    /// </summary>
    public double? GetProcessingTimeSeconds()
    {
        var started = Interlocked.Read(ref _processingStartedTimestamp);
        if (started == 0)
        {
            return null;
        }

        var finished = Interlocked.Read(ref _processingFinishedTimestamp);
        if (finished == 0)
        {
            finished = Stopwatch.GetTimestamp();
        }

        return Stopwatch.GetElapsedTime(started, finished).TotalSeconds;
    }

    public void Dispose() => ResultWriteLock.Dispose();
}

internal sealed class ProcessingUnit
{
    private DateTimeOffset? _queueEnteredAt;
    private TimeSpan _accumulatedQueueWait;

    public Guid Id { get; } = Guid.NewGuid();
    public required Guid DocumentId { get; init; }
    public required string ImagePath { get; init; }
    public int? PageNumber { get; init; }

    /// <summary>
    /// Cantidad de resultados finish_reason=error. stop y length son resultados
    /// aceptados y no incrementan este contador.
    /// </summary>
    public int VlmAttempts { get; private set; }
    public int AttemptNumber { get; private set; }

    public int InfrastructureFailures { get; private set; }
    public int GlobalFailures => VlmAttempts + InfrastructureFailures;

    public int ModelPriorityIndex { get; private set; }
    public string? LastFinishReason { get; private set; }
    public string? LastModel { get; private set; }
    public string? LastServer { get; private set; }
    public string? LastError { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsTerminalFailure { get; private set; }
    public DateTimeOffset NextEligibleAt { get; set; } = DateTimeOffset.MinValue;

    public DateTimeOffset? QueueEnteredAt => _queueEnteredAt;

    /// <summary>
    /// Métrica solamente informativa. No existe límite de tiempo de permanencia
    /// en cola; una unidad puede esperar mientras todavía tenga presupuesto de
    /// fallos y el lote no sea cancelado.
    /// </summary>
    public TimeSpan QueueWait
    {
        get
        {
            var current = _queueEnteredAt is { } entered
                ? DateTimeOffset.UtcNow - entered
                : TimeSpan.Zero;
            return _accumulatedQueueWait + current;
        }
    }

    public void EnterQueue()
    {
        if (_queueEnteredAt is null && !IsCompleted && !IsTerminalFailure)
        {
            _queueEnteredAt = DateTimeOffset.UtcNow;
        }
    }

    public void LeaveQueue()
    {
        if (_queueEnteredAt is not { } entered)
        {
            return;
        }

        _accumulatedQueueWait += DateTimeOffset.UtcNow - entered;
        _queueEnteredAt = null;
    }

    public void MarkAssignment(string serverName, string model)
    {
        LeaveQueue();
        AttemptNumber++;
        LastServer = serverName;
        LastModel = model;
        LastError = null;
    }

    public void Complete(Response result)
    {
        LeaveQueue();
        LastFinishReason = result.FinishReason;
        LastModel = result.Model ?? LastModel;
        LastError = null;
        IsCompleted = true;
        NextEligibleAt = DateTimeOffset.MaxValue;
    }

    /// <summary>Solo debe llamarse para finish_reason=error.</summary>
    public void RegisterVlmFailure(Response result, int maxAttempts, int priorityCount)
    {
        VlmAttempts++;
        LastFinishReason = result.FinishReason;
        LastModel = result.Model ?? LastModel;
        LastError = "El VLM retornó finish_reason=error.";

        if (priorityCount > 0)
        {
            ModelPriorityIndex = Math.Min(ModelPriorityIndex + 1, priorityCount - 1);
        }

        if (maxAttempts > 0 && VlmAttempts >= maxAttempts)
        {
            IsTerminalFailure = true;
        }
    }

    public void RegisterInfrastructureFailure(
        string error,
        TimeSpan delay,
        int maxInfrastructureFailures)
    {
        InfrastructureFailures++;
        LastError = error;
        NextEligibleAt = DateTimeOffset.UtcNow + delay;

        if (maxInfrastructureFailures > 0 &&
            InfrastructureFailures >= maxInfrastructureFailures)
        {
            IsTerminalFailure = true;
        }
    }

    public void EnforceGlobalFailureLimit(int maxGlobalFailures)
    {
        if (maxGlobalFailures <= 0 || GlobalFailures < maxGlobalFailures)
        {
            return;
        }

        LastError = $"La unidad alcanzó el límite global de {maxGlobalFailures} fallos.";
        IsTerminalFailure = true;
        NextEligibleAt = DateTimeOffset.MaxValue;
        LeaveQueue();
    }

    /// <summary>
    /// Espera operativa que no representa un fallo (servidor ocupado o en
    /// transición). No consume contadores.
    /// </summary>
    public void Delay(string reason, TimeSpan delay)
    {
        LastError = reason;
        NextEligibleAt = DateTimeOffset.UtcNow + delay;
    }

    public void FailPermanently(string error)
    {
        LeaveQueue();
        LastError = error;
        IsTerminalFailure = true;
        NextEligibleAt = DateTimeOffset.MaxValue;
    }
}
