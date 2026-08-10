namespace VlmHub.Balancer.Models;

public sealed record BatchResult(IReadOnlyList<ProcessedDocument> Documents);

public sealed record ProcessedDocument(
    string SourcePath,
    string? OutputPath,
    bool Completed,
    string? Error,
    IReadOnlyList<ProcessingUnitSummary> Units);

public sealed record ProcessingUnitSummary(
    int? PageNumber,
    bool Completed,
    int VlmAttempts,
    int InfrastructureFailures,
    string? FinishReason,
    string? Model,
    string? Server,
    string? LastError);
