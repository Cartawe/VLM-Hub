using PDFtoImage;
using SkiaSharp;
using VlmHub.Balancer.Configuration;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Orchestration;

internal sealed class DocumentPreprocessor
{
    private readonly ProcessingOptions _options;

    public DocumentPreprocessor(ProcessingOptions options)
    {
        _options = options;
    }

    public async Task<IReadOnlyList<PreparedDocument>> PrepareAsync(
        IReadOnlyList<string> documentPaths,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var prepared = new List<PreparedDocument>();

        foreach (var documentPath in documentPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isPdf = Path.GetExtension(documentPath)
                .Equals(".pdf", StringComparison.OrdinalIgnoreCase);

            try
            {
                prepared.Add(isPdf
                    ? await PreparePdfAsync(documentPath, workingDirectory, cancellationToken)
                    : PrepareImage(documentPath));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un documento corrupto o inaccesible no derriba el resto del lote.
                prepared.Add(new PreparedDocument
                {
                    SourcePath = documentPath,
                    Type = isPdf ? "pdf" : "imagen",
                    PreparationError = exception.Message
                });
            }
        }

        return prepared;
    }

    private static PreparedDocument PrepareImage(string documentPath)
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
            var document = new PreparedDocument
            {
                SourcePath = documentPath,
                Type = "pdf"
            };

            var pdfDirectory = Path.Combine(workingDirectory, document.Id.ToString("N"));
            Directory.CreateDirectory(pdfDirectory);

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
