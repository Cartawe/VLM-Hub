namespace VlmHub.Balancer.Models;

internal sealed class PreparedDocument
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public required string Type { get; init; }
    public string? PreparationError { get; set; }
    public IReadOnlyList<ProcessingUnit> Units { get; set; } = Array.Empty<ProcessingUnit>();
}

internal sealed class ProcessingUnit
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Guid DocumentId { get; init; }
    public required string ImagePath { get; init; }
    public int? PageNumber { get; init; }

    public int VlmAttempts { get; private set; }
    public int InfrastructureFailures { get; private set; }
    public int ModelPriorityIndex { get; private set; }
    public string? Content { get; private set; }
    public string? LastFinishReason { get; private set; }
    public string? LastModel { get; private set; }
    public string? LastServer { get; private set; }
    public string? LastError { get; private set; }
    public bool IsCompleted { get; private set; }
    public bool IsTerminalFailure { get; private set; }
    public DateTimeOffset NextEligibleAt { get; set; } = DateTimeOffset.MinValue;

    public void MarkServer(string serverName)
    {
        LastServer = serverName;
    }

    public void Complete(Response result)
    {
        Content = result.Content;
        LastFinishReason = result.FinishReason;
        LastModel = result.Model;
        LastError = null;
        IsCompleted = true;
    }

    public void RegisterVlmFailure(Response result, int maxAttempts, int priorityCount)
    {
        VlmAttempts++;
        LastFinishReason = result.FinishReason;
        LastModel = result.Model;
        LastError = null;

        // Un finish_reason=length puede contener una salida parcial útil. La
        // conservamos como último contenido conocido por si se agotan intentos.
        if (!string.IsNullOrWhiteSpace(result.Content))
        {
            Content = result.Content;
        }

        if (priorityCount > 0)
        {
            ModelPriorityIndex = Math.Min(ModelPriorityIndex + 1, priorityCount - 1);
        }

        if (VlmAttempts >= maxAttempts)
        {
            IsTerminalFailure = true;
        }
    }

    public void SkipUnavailablePriority(int newIndex)
    {
        ModelPriorityIndex = newIndex;
    }

    public void RegisterTransientFailure(
        string error,
        TimeSpan delay,
        int maxInfrastructureFailures)
    {
        InfrastructureFailures++;
        LastError = error;
        NextEligibleAt = DateTimeOffset.UtcNow + delay;

        if (InfrastructureFailures >= maxInfrastructureFailures)
        {
            IsTerminalFailure = true;
        }
    }

    public void FailPermanently(string error)
    {
        LastError = error;
        IsTerminalFailure = true;
    }
}
