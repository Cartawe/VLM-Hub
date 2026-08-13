namespace VlmHub.Sql.Persistence;

public sealed record LocalDocumentRecord(
    Guid Id,
    Guid ProcessingId,
    string ServerHost,
    string DatabaseName,
    string SourceTable,
    string OriginalKey,
    string? DocumentName,
    DateTimeOffset StartedAt);

public sealed record ProcessingUnitAttemptRecord(
    Guid DocumentId,
    int UnitIndex,
    int AttemptNumber,
    string? Model,
    string? Server,
    bool Success,
    string? FinishReason,
    double? ProcessingTime,
    DateTimeOffset ProcessedAt,
    string? Transcription,
    string? Error);
