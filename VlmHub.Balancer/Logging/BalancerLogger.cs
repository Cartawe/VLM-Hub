using System.Globalization;
using System.Text;

namespace VlmHub.Balancer.Logging;

/// <summary>
/// Traza operacional de un lote. Se mantiene un único archivo .log legible,
/// pensado para seguimiento humano y búsquedas con grep.
///
/// Los eventos de muy bajo nivel (cada heartbeat o cada respuesta HTTP correcta)
/// no se escriben para evitar ruido y crecimiento innecesario del archivo.
/// </summary>
internal sealed class BalancerLogger : IDisposable
{
    private readonly VlmHubLogger _logger;
    private readonly bool _ownsLogger;
    private readonly Guid? _processingId;
    private bool _disposed;

    public Guid BatchId { get; }
    public string LogPath => _logger.CurrentLogPath;

    private BalancerLogger(
        Guid batchId,
        string directory,
        Guid? processingId,
        VlmHubLogger? sharedLogger)
    {
        BatchId = batchId;
        _processingId = processingId;
        _logger = sharedLogger ?? new VlmHubLogger(directory);
        _ownsLogger = sharedLogger is null;
    }

    public static BalancerLogger Create(
        Guid batchId,
        string preferredDirectory,
        Guid? processingId = null,
        VlmHubLogger? sharedLogger = null) =>
        new(batchId, preferredDirectory, processingId, sharedLogger);

    public void Debug(string eventName, string message, LogContext? context = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("DEBUG", eventName, message, context, data, null);

    public void Info(string eventName, string message, LogContext? context = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("INFO", eventName, message, context, data, null);

    public void Warning(string eventName, string message, LogContext? context = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("WARN", eventName, message, context, data, null);

    public void Error(string eventName, string message, Exception? exception = null,
        LogContext? context = null, IReadOnlyDictionary<string, object?>? data = null) =>
        Write("ERROR", eventName, message, context, data, exception);

    private void Write(
        string level,
        string eventName,
        string message,
        LogContext? context,
        IReadOnlyDictionary<string, object?>? data,
        Exception? exception)
    {
        if (_disposed || !ShouldWrite(level, eventName))
        {
            return;
        }

        var detail = BuildDetail(eventName, message, context, data, exception);
        _logger.WriteBalancer(
            level,
            eventName,
            detail,
            _processingId,
            ExternalItemId(context),
            context?.Server ?? Value(data, "server"),
            context?.Model ?? Value(data, "model"));
    }

    private static bool ShouldWrite(string level, string eventName)
    {
        // DEBUG queda fuera del log operacional.
        if (level == "DEBUG")
        {
            return false;
        }

        // El detalle HTTP correcto es redundante con UNIT/MODEL/SERVER. Los
        // problemas de transporte se reflejan en esos eventos de nivel superior.
        if (eventName.StartsWith("api.", StringComparison.Ordinal))
        {
            return false;
        }

        if (level is "WARN" or "ERROR")
        {
            return true;
        }

        return eventName is
            "batch.started" or
            "batch.completed" or
            "batch.window.started" or
            "memory.snapshot" or
            "server.config.added" or
            "server.config.removed" or
            "server.online.initial" or
            "server.online" or
            "server.state" or
            "server.models.loaded" or
            "document.prepare.completed" or
            "pdf.temp_directory.created" or
            "scheduler.started" or
            "scheduler.waiting" or
            "scheduler.completed" or
            "unit.dispatched" or
            "unit.completed" or
            "result.initialized" or
            "server.recovery.completed" or
            "model.change.completed" or
            "model.bring_up.completed" or
            "model.bring_down.completed";
    }

    private string BuildDetail(
        string eventName,
        string message,
        LogContext? context,
        IReadOnlyDictionary<string, object?>? data,
        Exception? exception)
    {
        var doc = DocumentLabel(context);
        var server = context?.Server ?? Value(data, "server");
        var model = context?.Model ?? Value(data, "model");

        return eventName switch
        {
            "batch.started" =>
                $"inicio batch={BatchId:N} docs={Value(data, "documents") ?? "?"} modelos=[{FormatValue(data, "model_priority")}] temp={Value(data, "working_directory") ?? "?"}",

            "batch.window.started" =>
                $"ventana {Value(data, "window") ?? "?"} docs={Value(data, "documents") ?? "?"} rango={Value(data, "range") ?? "?"}",

            "batch.completed" =>
                $"fin ok={Value(data, "completed_documents") ?? "?"} fallidos={Value(data, "failed_documents") ?? "?"} duración={FormatDuration(data, "duration_ms")}",

            "memory.snapshot" =>
                $"{Value(data, "phase") ?? "muestra"} | ventana={Value(data, "window") ?? "-"} managed={Value(data, "managed_mb") ?? "?"}MB working={Value(data, "working_set_mb") ?? "?"}MB",

            "server.online.initial" =>
                $"{server} ONLINE estado={Value(data, "state") ?? "?"} modelo={Value(data, "active_model") ?? "-"}",

            "server.online" =>
                $"{server} REINCORPORADO estado={Value(data, "state") ?? "?"} modelo={Value(data, "active_model") ?? "-"}",

            "server.offline" =>
                $"{server} OFFLINE | {Compact(Value(data, "error") ?? message)}",

            "server.config.added" =>
                $"{server} AGREGADO url={Value(data, "url") ?? "?"}",

            "server.config.removed" =>
                $"{server} DESHABILITADO por configuración",

            "server.state" =>
                $"{server} estado={Value(data, "state") ?? "?"} modelo={Value(data, "active_model") ?? "-"}" +
                OptionalError(Value(data, "last_error")),

            "server.stuck" =>
                $"{server} ATASCADO estado={Value(data, "state") ?? "?"}" + OptionalError(Value(data, "last_error")),

            "server.models.loaded" =>
                $"{server} modelos=[{FormatValue(data, "models")}]",

            "pdf.temp_directory.created" =>
                $"{doc} temporales={context?.TemporaryPath ?? "?"}",

            "document.prepare.completed" =>
                $"{doc} preparado unidades={Value(data, "units") ?? "?"}",

            "scheduler.started" =>
                $"scheduler iniciado unidades={Value(data, "units") ?? "?"} servidores={Value(data, "servers") ?? "?"}",

            "scheduler.waiting" =>
                $"esperando capacidad | cola={Value(data, "pending_units") ?? "?"} online={Value(data, "online_servers") ?? "?"} libres={Value(data, "free_servers") ?? "?"} modelos=[{FormatValue(data, "required_models")}]",

            "unit.dispatched" =>
                $"{doc} -> {server} | modelo={model ?? "?"} | POST /api/process/images | cola={Value(data, "pending_units") ?? "?"}",

            "unit.completed" =>
                $"{doc} OK finish={Value(data, "finish_reason") ?? "?"} | {server} | modelo={model} | {FormatSeconds(data, "processing_seconds")}",

            "unit.vlm_retry" =>
                $"{doc} ERROR VLM {Value(data, "vlm_failures") ?? "?"}/{Value(data, "max_vlm_attempts") ?? "?"} | {server} | modelo={model} -> siguiente modelo",

            "unit.global_failures_exhausted" =>
                $"{doc} FALLÓ límite global={Value(data, "global_failures") ?? "?"}",

            "unit.infrastructure_failure" or
            "unit.processing_technical_failure" or
            "unit.model_prepare_failed" or
            "unit.server_recovery_failed" or
            "unit.unexpected_error" =>
                $"{doc} reencola | servidor={server} modelo={model} fallos={Value(data, "global_failures") ?? "?"} | {Compact(exception?.Message ?? Value(data, "reason") ?? message)}",

            "server.recovery.start" =>
                $"{server} RECUPERACIÓN bring-down -> bring-up | modelo={model}" + OptionalError(Value(data, "last_error")),

            "server.recovery.completed" =>
                $"{server} RECUPERADO modelo={model}",

            "server.recovery.failed" =>
                $"{server} RECUPERACIÓN FALLÓ modelo={model} | {Compact(exception?.Message ?? message)}",

            "model.change.completed" =>
                $"{server} modelo cambiado -> {model}",

            "model.bring_up.completed" =>
                $"{server} bring-up OK modelo={model}",

            "model.bring_down.completed" =>
                $"{server} bring-down OK",

            "result.initialized" =>
                $"{doc} resultado={Value(data, "output_path") ?? "?"}",

            "scheduler.completed" => "scheduler finalizado",

            _ =>
                $"{(doc == "-" ? string.Empty : doc + " | ")}{Compact(message)}" +
                (exception is null ? string.Empty : $" | {exception.GetType().Name}: {Compact(exception.Message)}")
        };
    }

    private static string Category(string eventName)
    {
        if (eventName.StartsWith("server.", StringComparison.Ordinal)) return "SERVER";
        if (eventName.StartsWith("unit.", StringComparison.Ordinal)) return "UNIT";
        if (eventName.StartsWith("model.", StringComparison.Ordinal)) return "MODEL";
        if (eventName.StartsWith("scheduler.", StringComparison.Ordinal)) return "QUEUE";
        if (eventName.StartsWith("document.", StringComparison.Ordinal) || eventName.StartsWith("pdf.", StringComparison.Ordinal)) return "DOCUMENT";
        if (eventName.StartsWith("result.", StringComparison.Ordinal)) return "RESULT";
        if (eventName.StartsWith("memory.", StringComparison.Ordinal)) return "MEMORY";
        if (eventName.StartsWith("batch.", StringComparison.Ordinal)) return "BATCH";
        return "SYSTEM";
    }

    private static string DocumentLabel(LogContext? context)
    {
        if (context?.SourcePath is null) return "-";
        var name = Path.GetFileName(context.SourcePath);
        return context.PageNumber is { } page ? $"{name}[p.{page:000}]" : name;
    }

    private static string? Value(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var value) || value is null) return null;
        return value switch
        {
            string text => text,
            Array array => string.Join(",", array.Cast<object?>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))),
            IEnumerable<string> strings => string.Join(",", strings),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static string FormatValue(IReadOnlyDictionary<string, object?>? data, string key) =>
        Value(data, key) ?? "-";

    private static string FormatDuration(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (double.TryParse(Value(data, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");
        }
        return "?";
    }

    private static string FormatSeconds(IReadOnlyDictionary<string, object?>? data, string key)
    {
        if (double.TryParse(Value(data, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
        {
            return $"{seconds:0.0}s";
        }
        return "?";
    }

    private static string OptionalError(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $" | {Compact(error)}";

    private static string Compact(string value)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 220 ? oneLine : oneLine[..217] + "...";
    }

    private static Guid? ExternalItemId(LogContext? context)
    {
        if (context?.SourcePath is null)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(context.SourcePath);
        return Guid.TryParseExact(name, "N", out var id) || Guid.TryParse(name, out id)
            ? id
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsLogger)
        {
            _logger.Dispose();
        }
    }

}

internal sealed record LogContext(
    Guid? DocumentId = null,
    Guid? UnitId = null,
    string? SourcePath = null,
    int? PageNumber = null,
    string? TemporaryPath = null,
    string? Server = null,
    string? Model = null)
{
    public LogContext WithServer(string server, string? model = null) =>
        this with { Server = server, Model = model ?? Model };
}
