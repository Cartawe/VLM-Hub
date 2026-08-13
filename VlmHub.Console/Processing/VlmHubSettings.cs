using System.Text.Json;
using System.Text.Json.Serialization;
using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Logging;

namespace VlmHub.Console.Processing;

internal sealed class VlmHubSettings
{
    [JsonPropertyName("servers_file")]
    public string ServersFile { get; init; } = "servidores.json";

    [JsonPropertyName("model_priority")]
    public IReadOnlyList<string> ModelPriority { get; init; } =
    [
        "PaddleOCR-VL-1.6-f16",
        "Qwen3VL-2B-Instruct-F16",
        "Deepseek-OCR-2-q8"
    ];

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; } = 3;

    [JsonPropertyName("max_documents_in_memory")]
    public int MaxDocumentsInMemory { get; init; } = 32;

    [JsonPropertyName("max_parallel_document_preparation")]
    public int MaxParallelDocumentPreparation { get; init; } = 2;

    [JsonPropertyName("max_parallel_downloads")]
    public int MaxParallelDownloads { get; init; } = 4;

    [JsonPropertyName("download_max_attempts")]
    public int DownloadMaxAttempts { get; init; } = 3;

    [JsonPropertyName("download_timeout_seconds")]
    public int DownloadTimeoutSeconds { get; init; } = 90;

    [JsonPropertyName("sql_upload_max_attempts")]
    public int SqlUploadMaxAttempts { get; init; } = 3;

    [JsonPropertyName("sql_upload_retry_seconds")]
    public int SqlUploadRetrySeconds { get; init; } = 3;

    [JsonPropertyName("sql_upload_cooldown_seconds")]
    public int SqlUploadCooldownSeconds { get; init; } = 30;

    [JsonPropertyName("keep_temporary_files_on_failure")]
    public bool KeepTemporaryFilesOnFailure { get; init; } = false;

    public static async Task<VlmHubSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "vlmhub.json"),
            Path.Combine(AppContext.BaseDirectory, "vlmhub.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return new VlmHubSettings();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<VlmHubSettings>(
                   stream,
                   cancellationToken: cancellationToken) ?? new VlmHubSettings();
    }

    public string ResolveServersFile()
    {
        var configured = Environment.GetEnvironmentVariable("VLMHUB_SERVERS_FILE");
        var path = string.IsNullOrWhiteSpace(configured) ? ServersFile : configured;
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    public ProcessingOptions ToBalancerOptions(string logDirectory, VlmHubLogger logger) => new()
    {
        MaxDocumentsInMemory = Math.Max(1, MaxDocumentsInMemory),
        PrefetchNextDocumentWindow = false,
        MaxParallelDocumentPreparation = Math.Max(1, MaxParallelDocumentPreparation),
        RetainUnitSummariesInBatchResult = false,
        LogDirectory = logDirectory,
        SharedLogger = logger,
        KeepTemporaryFilesOnFailure = KeepTemporaryFilesOnFailure
    };
}
