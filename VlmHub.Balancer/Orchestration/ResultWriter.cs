using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Orchestration;

internal sealed class ResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public async Task<string> WriteAsync(
        PreparedDocument document,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        var sourceName = Path.GetFileNameWithoutExtension(document.SourcePath);
        var outputPath = GetUniqueOutputPath(outputDirectory, sourceName);

        object result = document.Type == "pdf"
            ? BuildPdfResult(document)
            : BuildImageResult(document);

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);

        return outputPath;
    }

    private static object BuildImageResult(PreparedDocument document)
    {
        var content = document.Units.Count > 0
            ? document.Units[0].Content ?? string.Empty
            : string.Empty;

        return new
        {
            tipo = "imagen",
            contenido = content
        };
    }

    private static object BuildPdfResult(PreparedDocument document)
    {
        var content = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in document.Units.OrderBy(unit => unit.PageNumber))
        {
            content[$"pag_{unit.PageNumber:000}"] = unit.Content ?? string.Empty;
        }

        return new
        {
            tipo = "pdf",
            contenido = content
        };
    }

    private static string GetUniqueOutputPath(string directory, string sourceName)
    {
        var candidate = Path.Combine(directory, $"{sourceName}.json");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(
            directory,
            $"{sourceName}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmssfff}.json");
    }
}
