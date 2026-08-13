using System.Runtime.CompilerServices;
using System.Text.Json;

namespace VlmHub.Console.Processing;

internal static class SelectionManifestReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async IAsyncEnumerable<SelectionManifestItem> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<SelectionManifestItem>(
                           stream,
                           Options,
                           cancellationToken))
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}
