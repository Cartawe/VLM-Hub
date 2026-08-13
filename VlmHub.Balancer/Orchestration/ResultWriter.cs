using System.Text.Encodings.Web;
using System.Text.Json;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Orchestration;

/// <summary>
/// Mantiene el JSON de salida actualizado durante el procesamiento.
///
/// Para PDF, el texto de cada página se conserva temporalmente en un pequeño
/// archivo lateral dentro del directorio temporal del documento. Al actualizar
/// el JSON se lee una página a la vez, evitando retener el contenido completo
/// del PDF en memoria.
/// </summary>
internal sealed class ResultWriter
{
    private readonly BalancerLogger _logger;

    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ResultWriter(BalancerLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Crea inmediatamente el archivo final con todas sus unidades vacías.
    /// Desde este momento el usuario siempre dispone de un snapshot válido.
    /// </summary>
    public async Task<string> InitializeAsync(
        PreparedDocument document,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);

        var sourceName = Path.GetFileNameWithoutExtension(document.SourcePath);
        var outputPath = Path.Combine(outputDirectory, $"{sourceName}.json");

        await document.ResultWriteLock.WaitAsync(cancellationToken);
        try
        {
            if (document.Type == "pdf")
            {
                await WritePdfSnapshotAsync(document, outputPath, cancellationToken);
            }
            else
            {
                await WriteImageSnapshotAsync(outputPath, string.Empty, cancellationToken);
            }

            document.OutputPath = outputPath;
        }
        finally
        {
            document.ResultWriteLock.Release();
        }

        _logger.Info(
            "result.initialized",
            "Archivo de resultado inicializado.",
            new LogContext(DocumentId: document.Id, SourcePath: document.SourcePath),
            new Dictionary<string, object?> { ["output_path"] = outputPath });

        return outputPath;
    }

    /// <summary>
    /// Persiste el estado de una unidad inmediatamente después de recibir una
    /// respuesta VLM. En error se entrega contenido vacío; stop/length entregan
    /// el markdown ya saneado.
    /// </summary>
    public async Task UpdateUnitAsync(
        PreparedDocument document,
        ProcessingUnit unit,
        string content,
        CancellationToken cancellationToken)
    {
        var outputPath = document.OutputPath;
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new InvalidOperationException(
                $"El documento '{document.SourcePath}' no tiene un archivo de resultado inicializado.");
        }

        await document.ResultWriteLock.WaitAsync(cancellationToken);
        try
        {
            if (document.Type == "pdf")
            {
                await PersistPageContentAsync(document, unit, content, cancellationToken);
                await WritePdfSnapshotAsync(document, outputPath, cancellationToken);
            }
            else
            {
                await WriteImageSnapshotAsync(outputPath, content, cancellationToken);
            }
        }
        finally
        {
            document.ResultWriteLock.Release();
        }
    }

    public async Task MarkCompletedAsync(
        PreparedDocument document,
        ProcessedDocument summary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(document.OutputPath))
        {
            return;
        }

        var markerPath = GetCompletionMarkerPath(document.OutputPath);
        var tempPath = BuildAtomicTempPath(markerPath);
        var marker = new ResultCompletionMarker(
            summary.Completed,
            summary.NumPages,
            summary.ProcessingTime,
            summary.Error,
            DateTimeOffset.Now);

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                8 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, marker, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, markerPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static string GetCompletionMarkerPath(string outputPath) =>
        $"{outputPath}.ready";

    private static async Task PersistPageContentAsync(
        PreparedDocument document,
        ProcessingUnit unit,
        string content,
        CancellationToken cancellationToken)
    {
        if (unit.PageNumber is not { } pageNumber)
        {
            throw new InvalidOperationException("Una unidad PDF no tiene número de página.");
        }

        var stateDirectory = document.StateDirectory;
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new InvalidOperationException("El PDF no tiene directorio temporal de estado.");
        }

        Directory.CreateDirectory(stateDirectory);
        var statePath = GetPageStatePath(stateDirectory, pageNumber);
        await File.WriteAllTextAsync(statePath, content, cancellationToken);
    }

    private static async Task WritePdfSnapshotAsync(
        PreparedDocument document,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var tempPath = BuildAtomicTempPath(outputPath);

        try
        {
            {
                await using var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                using (var json = new Utf8JsonWriter(stream, JsonOptions))
                {
                    json.WriteStartObject();
                    json.WriteString("tipo", "pdf");
                    json.WritePropertyName("contenido");
                    json.WriteStartObject();

                    foreach (var unit in document.Units.OrderBy(unit => unit.PageNumber))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var page = unit.PageNumber ?? 0;
                        var content = string.Empty;

                        var stateDirectory = document.StateDirectory;
                        if (!string.IsNullOrWhiteSpace(stateDirectory))
                        {
                            var statePath = GetPageStatePath(stateDirectory, page);
                            if (File.Exists(statePath))
                            {
                                // Solo una página se carga en RAM a la vez.
                                content = await File.ReadAllTextAsync(statePath, cancellationToken);
                            }
                        }

                        json.WriteString($"pag_{page:000}", content);
                    }

                    json.WriteEndObject();
                    json.WriteEndObject();
                    json.Flush();
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            // El stream debe estar cerrado antes del reemplazo, especialmente
            // en Windows donde FileShare.None impide renombrar un archivo abierto.
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task WriteImageSnapshotAsync(
        string outputPath,
        string content,
        CancellationToken cancellationToken)
    {
        var tempPath = BuildAtomicTempPath(outputPath);

        try
        {
            {
                await using var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                using (var json = new Utf8JsonWriter(stream, JsonOptions))
                {
                    json.WriteStartObject();
                    json.WriteString("tipo", "imagen");
                    json.WriteString("contenido", content);
                    json.WriteEndObject();
                    json.Flush();
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static string GetPageStatePath(string directory, int pageNumber) =>
        Path.Combine(directory, $"result_pag_{pageNumber:000}.txt");

    private static string BuildAtomicTempPath(string outputPath) =>
        $"{outputPath}.{Guid.NewGuid():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Un temporal huérfano no debe ocultar el error original.
        }
    }

}
