using System.Net;
using System.Net.Http.Headers;

namespace VlmHub.Console.Processing;

internal sealed class DocumentDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly VlmHubSettings _settings;

    public DocumentDownloader(VlmHubSettings settings)
    {
        _settings = settings;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(2, settings.MaxParallelDownloads)
        };
        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<string> DownloadAsync(
        string documentUrl,
        string destinationBasePath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var attempts = Math.Max(1, _settings.DownloadMaxAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partialPath = $"{destinationBasePath}.download.tmp";
            TryDelete(partialPath);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.DownloadTimeoutSeconds)));

                using var response = await _client.GetAsync(
                    documentUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token))
                await using (var target = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, 64 * 1024, timeoutCts.Token);
                    await target.FlushAsync(timeoutCts.Token);
                    target.Flush(flushToDisk: true);
                }

                var extension = ResolveExtension(documentUrl, response.Content.Headers.ContentType);
                var finalPath = destinationBasePath + extension;
                File.Move(partialPath, finalPath, overwrite: true);
                return finalPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partialPath);
                throw;
            }
            catch (Exception exception)
            {
                TryDelete(partialPath);
                lastError = exception;

                if (attempt < attempts)
                {
                    var delaySeconds = Math.Min(10, 1 << Math.Min(attempt, 3));
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                }
            }
        }

        throw new IOException(
            $"No fue posible obtener el documento después de {attempts} intento(s).",
            lastError);
    }

    private static string ResolveExtension(string url, MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType?.ToLowerInvariant();
        if (mediaType == "application/pdf") return ".pdf";
        if (mediaType == "image/jpeg") return ".jpg";
        if (mediaType == "image/png") return ".png";
        if (mediaType == "image/tiff") return ".tif";
        if (mediaType == "image/webp") return ".webp";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 12)
            {
                return extension;
            }
        }

        return ".bin";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // La limpieza de un parcial no debe esconder el error de descarga.
        }
    }

    public void Dispose() => _client.Dispose();
}
