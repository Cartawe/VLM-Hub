using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using VlmHub.Balancer.Models;

namespace VlmHub.Balancer.Api;

/// <summary>
/// Cliente HTTP del contrato de VLM Server API. El único endpoint documental
/// usado por este módulo es /api/process/images.
/// </summary>
internal sealed class VlmServerClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ServerDefinition Definition { get; }

    public VlmServerClient(ServerDefinition definition)
    {
        Definition = definition;
        _httpClient = new HttpClient
        {
            BaseAddress = definition.BuildBaseUri(),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<Status> GetStatusAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "api/status"),
            timeout,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        EnsureSuccessfulEnvelope(response, json);

        var data = GetObject(json.RootElement, "data");

        return new Status(
            GetString(data, "state") ?? "Desconocido",
            GetString(data, "active_model"),
            GetString(data, "last_error"),
            GetDateTimeOffset(data, "last_state_change"));
    }

    public async Task<IReadOnlySet<string>> GetModelsAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "api/model/list"),
            timeout,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);
        EnsureSuccessfulEnvelope(response, json);

        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (json.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var model = GetString(item, "model-name");
                if (!string.IsNullOrWhiteSpace(model))
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    public async Task EnsureModelAsync(
        string model,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var encodedModel = Uri.EscapeDataString(model);

        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Post,
                $"api/model/{encodedModel}/change"),
            timeout,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var message = GetMessage(json.RootElement) ?? "El servidor no pudo cambiar de modelo.";

            if (message.Contains("No se pudo cargar", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No se pudo detener", StringComparison.OrdinalIgnoreCase))
            {
                throw new VlmModelUnavailableException(model, message, response.StatusCode);
            }

            throw new VlmServerBusyException(message, response.StatusCode);
        }

        EnsureSuccessfulEnvelope(response, json);
    }

    public async Task<Response> ProcessImageAsync(
        string imagePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => CreateImageRequest(imagePath),
            timeout,
            cancellationToken);

        using var json = await ReadJsonAsync(response, cancellationToken);

        // Algunos 422 de procesamiento sí traen result con finish reason=error.
        // Ese caso es un intento VLM válido y debe ser contabilizado.
        if (TryReadImageResult(json.RootElement, out var result))
        {
            return result;
        }

        var message = GetMessage(json.RootElement) ??
                      $"VLM Server respondió HTTP {(int)response.StatusCode}.";

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
            (message.Contains("ocupado", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("estado actual", StringComparison.OrdinalIgnoreCase)))
        {
            throw new VlmServerBusyException(message, response.StatusCode);
        }

        if (response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.InternalServerError)
        {
            throw new VlmServerTransientException(message, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new VlmRequestException(message, response.StatusCode);
        }

        EnsureSuccessfulEnvelope(response, json);
        throw new VlmApiException("La respuesta no contiene un resultado VLM utilizable.", response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var request = requestFactory();
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VlmServerTransientException(
                $"{Definition.Name} no respondió dentro de {timeout}.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new VlmServerTransientException(
                $"No fue posible comunicarse con {Definition.Name}: {exception.Message}",
                innerException: exception);
        }
    }

    private static HttpRequestMessage CreateImageRequest(string imagePath)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/process/images");
        var multipart = new MultipartFormDataContent();
        var stream = File.OpenRead(imagePath);
        var fileContent = new StreamContent(stream);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetImageContentType(imagePath));
        multipart.Add(fileContent, "file", Path.GetFileName(imagePath));
        request.Content = multipart;

        return request;
    }

    private static string GetImageContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "image/png"
        };

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new VlmApiException(
                $"El servidor respondió HTTP {(int)response.StatusCode} con un cuerpo JSON inválido.",
                response.StatusCode,
                exception);
        }
    }

    private static void EnsureSuccessfulEnvelope(
        HttpResponseMessage response,
        JsonDocument json)
    {
        var root = json.RootElement;
        var success = root.TryGetProperty("success", out var successElement) &&
                      successElement.ValueKind is JsonValueKind.True;

        if (response.IsSuccessStatusCode && success)
        {
            return;
        }

        var message = GetMessage(root) ??
                      $"VLM Server respondió HTTP {(int)response.StatusCode}.";

        if (response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.InternalServerError)
        {
            throw new VlmServerTransientException(message, response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new VlmServerBusyException(message, response.StatusCode);
        }

        throw new VlmApiException(message, response.StatusCode);
    }

    private static bool TryReadImageResult(
        JsonElement root,
        out Response result)
    {
        result = default!;

        if (!root.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var finishReason = GetString(resultElement, "finish reason");
        if (string.IsNullOrWhiteSpace(finishReason))
        {
            return false;
        }

        var content = GetString(resultElement, "md") ?? string.Empty;
        var model = GetString(resultElement, "model");
        double? time = null;

        if (resultElement.TryGetProperty("time", out var timeElement) &&
            timeElement.TryGetDouble(out var parsedTime))
        {
            time = parsedTime;
        }

        result = new Response(
            finishReason.Trim().ToLowerInvariant(),
            content,
            model,
            time);

        return true;
    }

    private static JsonElement GetObject(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        return default;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        var value = GetString(element, name);

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? GetMessage(JsonElement root) => GetString(root, "message");

    public void Dispose() => _httpClient.Dispose();
}
