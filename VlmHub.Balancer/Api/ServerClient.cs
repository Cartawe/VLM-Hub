using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VlmHub.Balancer.Logging;
using VlmHub.Balancer.Models;
using VlmHub.Balancer.Orchestration;

namespace VlmHub.Balancer.Api;

/// <summary>
/// Cliente HTTP del contrato de VLM Server API. El único endpoint documental
/// utilizado es /api/process/images.
/// </summary>
internal sealed class VlmServerClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly BalancerLogger _logger;

    public ServerDefinition Definition { get; }

    public VlmServerClient(ServerDefinition definition, BalancerLogger logger)
    {
        Definition = definition;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = definition.BuildBaseUri(),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<Status> GetStatusAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext? context = null)
    {
        const string endpoint = "api/status";
        using var response = await SendAsync(
            HttpMethod.Get,
            endpoint,
            () => new HttpRequestMessage(HttpMethod.Get, endpoint),
            timeout,
            cancellationToken,
            context,
            logRequest: false,
            logTransportErrors: false);

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
        CancellationToken cancellationToken,
        LogContext? context = null)
    {
        const string endpoint = "api/model/list";
        using var response = await SendAsync(
            HttpMethod.Get,
            endpoint,
            () => new HttpRequestMessage(HttpMethod.Get, endpoint),
            timeout,
            cancellationToken,
            context,
            logRequest: false,
            logTransportErrors: false);

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

    /// <summary>
    /// Prepara el modelo de acuerdo al estado observado del daemon.
    /// En ErrorModelo/ErrorDaemon NO usa change-model: ejecuta bring-down y
    /// después bring-up, que es la secuencia administrativa de recuperación.
    /// </summary>
    public async Task EnsureModelAsync(
        string model,
        Status status,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext? context = null)
    {
        var effectiveContext = EnrichContext(context, model);

        if (IsErrorState(status.State))
        {
            _logger.Warning(
                "server.recovery.start",
                $"{Definition.Name} está en {status.State}. Se iniciará recuperación administrativa.",
                effectiveContext,
                new Dictionary<string, object?>
                {
                    ["state"] = status.State,
                    ["active_model"] = status.ActiveModel,
                    ["last_error"] = status.LastError
                });

            await RecoverModelAsync(model, timeout, cancellationToken, effectiveContext);
            return;
        }

        if (status.State.Equals("SinModelo", StringComparison.OrdinalIgnoreCase))
        {
            await BringUpAsync(model, timeout, cancellationToken, effectiveContext);
            return;
        }

        if (status.State.Equals("Disponible", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status.ActiveModel, model, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await ChangeModelAsync(model, timeout, cancellationToken, effectiveContext);
            return;
        }

        throw new VlmServerBusyException(
            $"{Definition.Name} no está listo para preparar modelo. Estado actual: {status.State}.");
    }

    public async Task<Response> ProcessImageAsync(
        string imagePath,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext? context = null)
    {
        const string endpoint = "api/process/images";
        using var response = await SendAsync(
            HttpMethod.Post,
            endpoint,
            () => CreateImageRequest(imagePath),
            timeout,
            cancellationToken,
            context,
            logRequest: true);

        using var json = await ReadJsonAsync(response, cancellationToken);

        // Algunos 422 de procesamiento traen result con finish reason=error.
        // error es un intento VLM real. stop y length son resultados aceptables.
        if (TryReadImageResult(json.RootElement, out var result))
        {
            // stop y length son resultados aceptados y requieren una respuesta
            // HTTP/success válida. error puede venir en una respuesta 422.
            var accepted = result.FinishReason.Equals("stop", StringComparison.OrdinalIgnoreCase) ||
                           result.FinishReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
                           result.FinishReason.Equals("lenght", StringComparison.OrdinalIgnoreCase);

            if (accepted &&
                (!response.IsSuccessStatusCode || !IsEnvelopeSuccess(json.RootElement)))
            {
                throw new VlmProcessingTechnicalException(
                    $"El servidor retornó finish_reason={result.FinishReason} sin una respuesta HTTP/success válida.",
                    response.StatusCode);
            }

            return result;
        }

        var message = GetMessage(json.RootElement) ??
                      $"VLM Server respondió HTTP {(int)response.StatusCode}.";

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            if (message.Contains("ocupado", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("estado actual", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No se puede procesar", StringComparison.OrdinalIgnoreCase))
            {
                throw new VlmServerBusyException(message, response.StatusCode);
            }

            // 422 sin finish_reason documental: fallo técnico/funcional del
            // procesamiento. Mantiene el modelo, pero no se trata como ocupado.
            throw new VlmProcessingTechnicalException(message, response.StatusCode);
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
        throw new VlmProcessingTechnicalException(
            "La respuesta no contiene un resultado VLM utilizable.",
            response.StatusCode);
    }

    private async Task RecoverModelAsync(
        string model,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext context)
    {
        try
        {
            await BringDownAsync(timeout, cancellationToken, context);
            await BringUpAsync(model, timeout, cancellationToken, context);

            _logger.Info(
                "server.recovery.completed",
                $"{Definition.Name} fue recuperado y el modelo '{model}' quedó preparado.",
                context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error(
                "server.recovery.failed",
                $"Falló la recuperación administrativa de {Definition.Name}.",
                exception,
                context);
            throw;
        }
    }

    private async Task BringDownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext context)
    {
        const string endpoint = "api/model/bring-down";
        using var response = await SendAsync(
            HttpMethod.Post,
            endpoint,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint),
            timeout,
            cancellationToken,
            context,
            logRequest: true);

        using var json = await ReadJsonAsync(response, cancellationToken);
        var message = GetMessage(json.RootElement) ?? "Respuesta de bring-down sin mensaje.";

        // Si el daemon ya no tiene modelo activo, el objetivo de bring-down
        // ya está cumplido y se puede continuar con bring-up.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity &&
            message.Contains("No hay un modelo activo", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info(
                "model.bring_down.already_empty",
                message,
                context);
            return;
        }

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            if (message.Contains("ocupado", StringComparison.OrdinalIgnoreCase))
            {
                throw new VlmServerBusyException(message, response.StatusCode);
            }

            throw new VlmServerRecoveryException(message, response.StatusCode);
        }

        EnsureSuccessfulEnvelope(response, json);

        _logger.Info(
            "model.bring_down.completed",
            message,
            context,
            new Dictionary<string, object?>
            {
                ["http_status"] = (int)response.StatusCode,
                ["endpoint"] = "/api/model/bring-down"
            });
    }

    private async Task BringUpAsync(
        string model,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext context)
    {
        var encodedModel = Uri.EscapeDataString(model);
        var endpoint = $"api/model/{encodedModel}/bring-up";

        using var response = await SendAsync(
            HttpMethod.Post,
            endpoint,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint),
            timeout,
            cancellationToken,
            context,
            logRequest: true);

        using var json = await ReadJsonAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var message = GetMessage(json.RootElement) ??
                          $"No fue posible cargar el modelo '{model}'.";

            if (message.Contains("ocupado", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("estado actual", StringComparison.OrdinalIgnoreCase))
            {
                throw new VlmServerBusyException(message, response.StatusCode);
            }

            throw new VlmModelUnavailableException(model, message, response.StatusCode);
        }

        EnsureSuccessfulEnvelope(response, json);
        EnsureModelReady(json.RootElement, model, response.StatusCode);

        _logger.Info(
            "model.bring_up.completed",
            GetMessage(json.RootElement) ?? $"Modelo '{model}' cargado.",
            context,
            new Dictionary<string, object?>
            {
                ["http_status"] = (int)response.StatusCode,
                ["endpoint"] = $"/api/model/{encodedModel}/bring-up"
            });
    }

    private async Task ChangeModelAsync(
        string model,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext context)
    {
        var encodedModel = Uri.EscapeDataString(model);
        var endpoint = $"api/model/{encodedModel}/change";

        using var response = await SendAsync(
            HttpMethod.Post,
            endpoint,
            () => new HttpRequestMessage(HttpMethod.Post, endpoint),
            timeout,
            cancellationToken,
            context,
            logRequest: true);

        using var json = await ReadJsonAsync(response, cancellationToken);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var message = GetMessage(json.RootElement) ??
                          "El servidor no pudo cambiar de modelo.";

            // Si change-model dejó al daemon en ErrorModelo/ErrorDaemon y la
            // respuesta incluye el estado, se recupera inmediatamente con la
            // secuencia administrativa bring-down -> bring-up.
            var data = GetObject(json.RootElement, "data");
            var resultingState = GetString(data, "state");

            if (!string.IsNullOrWhiteSpace(resultingState) && IsErrorState(resultingState))
            {
                _logger.Warning(
                    "server.recovery.start",
                    $"{Definition.Name} entró en {resultingState} durante change-model; se usará bring-down -> bring-up.",
                    context,
                    new Dictionary<string, object?>
                    {
                        ["state"] = resultingState,
                        ["active_model"] = GetString(data, "active_model"),
                        ["last_error"] = GetString(data, "last_error")
                    });

                await RecoverModelAsync(model, timeout, cancellationToken, context);
                return;
            }

            if (message.Contains("No se pudo cargar", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No se pudo detener", StringComparison.OrdinalIgnoreCase))
            {
                throw new VlmModelUnavailableException(model, message, response.StatusCode);
            }

            throw new VlmServerBusyException(message, response.StatusCode);
        }

        EnsureSuccessfulEnvelope(response, json);
        EnsureModelReady(json.RootElement, model, response.StatusCode);

        _logger.Info(
            "model.change.completed",
            GetMessage(json.RootElement) ?? $"Modelo activo cambiado a '{model}'.",
            context,
            new Dictionary<string, object?>
            {
                ["http_status"] = (int)response.StatusCode,
                ["endpoint"] = $"/api/model/{encodedModel}/change"
            });
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string endpoint,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        LogContext? context,
        bool logRequest,
        bool logTransportErrors = true)
    {
        // Los eventos HTTP de bajo nivel ya no se persisten. El .log registra
        // acciones operacionales (UNIT/MODEL/SERVER) y sus errores, evitando
        // duplicidad y reduciendo asignaciones por cada heartbeat/request.
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
                $"{Definition.Name} no respondió a {method.Method} /{endpoint} dentro de {timeout}.",
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

    private static bool IsEnvelopeSuccess(JsonElement root) =>
        root.TryGetProperty("success", out var successElement) &&
        successElement.ValueKind is JsonValueKind.True;

    private static void EnsureModelReady(
        JsonElement root,
        string requestedModel,
        HttpStatusCode statusCode)
    {
        var data = GetObject(root, "data");
        var state = GetString(data, "state");
        var activeModel = GetString(data, "active_model");

        if (state?.Equals("Disponible", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(activeModel, requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new VlmProcessingTechnicalException(
            $"La operación de modelo respondió éxito, pero no confirmó state=Disponible y active_model='{requestedModel}'. " +
            $"state='{state ?? "null"}', active_model='{activeModel ?? "null"}'.",
            statusCode);
    }

    private static bool TryReadImageResult(JsonElement root, out Response result)
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

        var content = VlmContentSanitizer.Clean(GetString(resultElement, "md"));
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

    private LogContext EnrichContext(LogContext? context, string? model) =>
        (context ?? new LogContext()) with
        {
            Server = Definition.Name,
            Model = model ?? context?.Model
        };

    private static bool IsErrorState(string state) =>
        state.Equals("ErrorModelo", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ErrorDaemon", StringComparison.OrdinalIgnoreCase);

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
