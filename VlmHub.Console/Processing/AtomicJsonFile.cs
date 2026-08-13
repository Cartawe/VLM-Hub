using System.Text.Json;

namespace VlmHub.Console.Processing;

/// <summary>
/// Escritura durable mediante archivo lateral + rename. Un .tmp nunca se toma
/// como estado válido después de una caída.
/// </summary>
internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task<T?> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
    }

    public static void DeleteOrphanTemps(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories))
        {
            TryDelete(path);
        }
    }

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
            // La limpieza nunca debe ocultar el error que originó el temporal.
        }
    }
}
