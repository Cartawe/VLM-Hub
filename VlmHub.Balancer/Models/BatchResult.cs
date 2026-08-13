namespace VlmHub.Balancer.Models;

public sealed record BatchResult(IReadOnlyList<ProcessedDocument> Documents)
{
    public Guid BatchId { get; init; }
    public string? LogPath { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? FatalError { get; init; }
}

public sealed record ProcessedDocument(
    string SourcePath,
    string? OutputPath,
    bool Completed,
    int? NumPages,
    double? ProcessingTime,
    string? Error,
    IReadOnlyList<ProcessingUnitSummary> Units);

public sealed record ResultCompletionMarker(
    bool Success,
    int? NumPages,
    double? ProcessingTime,
    string? Error,
    DateTimeOffset CompletedAt);

public sealed record ProcessingUnitSummary(
    int? PageNumber,
    bool Completed,
    int VlmAttempts,
    int InfrastructureFailures,
    int GlobalFailures,
    double QueueWaitSeconds,
    string? FinishReason,
    string? Model,
    string? Server,
    string? LastError);
