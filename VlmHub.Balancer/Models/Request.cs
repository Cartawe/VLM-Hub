namespace VlmHub.Balancer.Models;

/// <summary>
/// Entrada pública del módulo. Puede contener un documento o una colección.
/// </summary>
public sealed class ProcessingRequest
{
    public required IReadOnlyList<string> Documents { get; init; }
    public required IReadOnlyList<string> ModelPriority { get; init; }
    public required int MaxAttempts { get; init; }
    public required string ServersFile { get; init; }
    public required string OutputDirectory { get; init; }
}
