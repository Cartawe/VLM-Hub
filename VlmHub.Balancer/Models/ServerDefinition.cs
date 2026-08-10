using System.Text.Json.Serialization;

namespace VlmHub.Balancer.Models;

/// <summary>
/// Registro leído desde servidores.json.
/// </summary>
public sealed class ServerDefinition
{
    public required string Name { get; init; }

    [JsonPropertyName("URL")]
    public required string Url { get; init; }

    [JsonPropertyName("port")]
    public required string Port { get; init; }

    public string? Description { get; init; }

    public Uri BuildBaseUri()
    {
        var address = Url.Contains("://", StringComparison.Ordinal)
            ? Url
            : $"http://{Url}";

        var builder = new UriBuilder(address)
        {
            Port = int.Parse(Port, System.Globalization.CultureInfo.InvariantCulture)
        };

        return builder.Uri;
    }
}
