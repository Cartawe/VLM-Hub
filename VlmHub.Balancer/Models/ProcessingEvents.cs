namespace VlmHub.Balancer.Models;

/// <summary>
/// Snapshot de un despacho terminado. Se emite de forma progresiva para que el
/// llamador persista historial sin conservar todos los intentos del lote.
/// </summary>
public sealed record ProcessingAttemptEvent(
    string SourcePath,
    int UnitIndex,
    int AttemptNumber,
    string? Model,
    string? Server,
    bool Success,
    string? FinishReason,
    double? ProcessingSeconds,
    DateTimeOffset ProcessedAt,
    string? Transcription,
    string? Error);
