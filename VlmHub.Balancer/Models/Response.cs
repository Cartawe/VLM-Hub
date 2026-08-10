namespace VlmHub.Balancer.Models;

/// <summary>
/// Resultado útil retornado por /api/process/images.
/// </summary>
public sealed record Response(
    string FinishReason,
    string Content,
    string? Model,
    double? ProcessingSeconds);
