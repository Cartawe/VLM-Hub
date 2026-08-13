using System.Text.Json;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Configuration;

internal static class ServerConfiguration
{
    public static async Task<IReadOnlyList<ServerDefinition>> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        // FileShare permite que un administrador reemplace/edite el JSON
        // mientras el monitor conserva la última configuración válida.
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<ServerDefinition[]>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                   cancellationToken)
               ?? Array.Empty<ServerDefinition>();
    }
}
