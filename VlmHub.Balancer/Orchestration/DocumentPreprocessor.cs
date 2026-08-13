using PDFtoImage;
using SkiaSharp;
using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Orchestration;

internal sealed class DocumentPreprocessor
{
    private readonly ProcessingOptions _options;
    private readonly BalancerLogger _logger;

    public DocumentPreprocessor(ProcessingOptions options, BalancerLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PreparedDocument>> PrepareAsync(
        IReadOnlyList<string> documentPaths,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var prepared = new PreparedDocument[documentPaths.Count];

        _logger.Info(
            "temp.directory.created",
            "Directorio temporal del lote creado.",
            data: new Dictionary<string, object?> { ["working_directory"] = workingDirectory });

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelDocumentPreparation)
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, documentPaths.Count),
            parallelOptions,
            async (index, token) =>
            {
                var documentPath = documentPaths[index];
                var isPdf = Path.GetExtension(documentPath)
                    .Equals(".pdf", StringComparison.OrdinalIgnoreCase);

                try
                {
                    var document = isPdf
                        ? await PreparePdfAsync(documentPath, workingDirectory, token)
                        : PrepareImage(documentPath);

                    prepared[index] = document;

                    _logger.Info(
                        "document.prepare.completed",
                        $"Documento preparado en {document.Units.Count} unidad(es).",
                        new LogContext(DocumentId: document.Id, SourcePath: document.SourcePath),
                        new Dictionary<string, object?>
                        {
                            ["document_type"] = document.Type,
                            ["units"] = document.Units.Count
                        });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failed = new PreparedDocument
                    {
                        SourcePath = documentPath,
                        Type = isPdf ? "pdf" : "imagen",
                        PreparationError = exception.Message
                    };
                    prepared[index] = failed;

                    _logger.Error(
                        "document.prepare.failed",
                        "No fue posible preparar el documento; el lote continuará.",
                        exception,
                        new LogContext(DocumentId: failed.Id, SourcePath: documentPath));
                }
            });

        return prepared;
    }

    private PreparedDocument PrepareImage(string documentPath)
    {
        var document = new PreparedDocument
        {
            SourcePath = documentPath,
            Type = "imagen"
        };

        document.Units =
        [
            new ProcessingUnit
            {
                DocumentId = document.Id,
                ImagePath = documentPath,
                PageNumber = null
            }
        ];

        return document;
    }

    private Task<PreparedDocument> PreparePdfAsync(
        string documentPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "La rasterización PDF de VLMHub está soportada en Windows y Linux.");
            }

            var document = new PreparedDocument
            {
                SourcePath = documentPath,
                Type = "pdf"
            };

            var pdfDirectory = Path.Combine(workingDirectory, document.Id.ToString("N"));
            document.StateDirectory = pdfDirectory;
            Directory.CreateDirectory(pdfDirectory);

            _logger.Info(
                "pdf.temp_directory.created",
                "Directorio temporal del PDF creado.",
                new LogContext(
                    DocumentId: document.Id,
                    SourcePath: documentPath,
                    TemporaryPath: pdfDirectory),
                new Dictionary<string, object?> { ["pdf_dpi"] = _options.PdfDpi });

            var units = new List<ProcessingUnit>();
            var renderOptions = new RenderOptions(Dpi: _options.PdfDpi, UseTiling: true);

            using var pdfStream = File.OpenRead(documentPath);
            var pageNumber = 1;

            foreach (var bitmap in Conversion.ToImages(pdfStream, options: renderOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using (bitmap)
                {
                    var imagePath = Path.Combine(pdfDirectory, $"pag_{pageNumber:000}.png");
                    using var imageStream = File.Create(imagePath);

                    if (!bitmap.Encode(imageStream, SKEncodedImageFormat.Png, 100))
                    {
                        throw new IOException(
                            $"No fue posible rasterizar la página {pageNumber} de {documentPath}.");
                    }

                    units.Add(new ProcessingUnit
                    {
                        DocumentId = document.Id,
                        ImagePath = imagePath,
                        PageNumber = pageNumber
                    });
                }

                pageNumber++;
            }

            if (units.Count == 0)
            {
                throw new InvalidDataException(
                    $"El PDF '{documentPath}' no produjo páginas rasterizables.");
            }

            document.Units = units;
            return document;
        }, cancellationToken);
    }
}
