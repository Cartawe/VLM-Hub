using System.Text.Json;
using System.Text.Json.Serialization;

namespace VlmHub.Console.Processing;

internal static class ProcessingStates
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string PendingUpload = "PendingUpload";
    public const string Completed = "Completed";
}

internal sealed class ProcessingRunControl
{
    [JsonPropertyName("processing_id")]
    public required Guid ProcessingId { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("server")]
    public required string Server { get; init; }

    [JsonPropertyName("database")]
    public required string Database { get; init; }

    [JsonPropertyName("table")]
    public required string Table { get; init; }

    [JsonPropertyName("document_column")]
    public required string DocumentColumn { get; init; }

    [JsonPropertyName("primary_key_columns")]
    public required IReadOnlyList<string> PrimaryKeyColumns { get; init; }

    [JsonPropertyName("auxiliary_table")]
    public string? AuxiliaryTable { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "creating";

    [JsonPropertyName("success")]
    public long? Success { get; set; }

    [JsonPropertyName("failed")]
    public long? Failed { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double? DurationSeconds { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }
}

internal sealed class ProcessingItemControl
{
    [JsonPropertyName("item_id")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("state")]
    public string State { get; set; } = ProcessingStates.Pending;

    [JsonPropertyName("success")]
    public bool? Success { get; set; }
    
    [JsonPropertyName("num_pages")]
    public int? NumPages { get; set; }

    [JsonPropertyName("result_path")]
    public string? ResultPath { get; set; }

    [JsonPropertyName("server_processing_id")]
    public long? ServerProcessingId { get; set; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    [JsonPropertyName("processing_time")]
    public double? ProcessingTime { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

internal sealed class SelectionManifestItem
{
    [JsonPropertyName("item_id")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("primary_key")]
    public required Dictionary<string, JsonElement> PrimaryKey { get; init; }

    [JsonPropertyName("document_url")]
    public string? DocumentUrl { get; init; }
}

internal sealed record ProcessingRunSummary(
    string Directory,
    ProcessingRunControl Run,
    long Completed,
    long Pending);
