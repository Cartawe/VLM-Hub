using System.Globalization;
using System.Text;

namespace VlmHub.Balancer.Logging;

/// <summary>
/// Logger común del proceso VLMHub. Un único escritor maneja consola, SQL,
/// downloader, SQLite y balanceador. Rota por día y por tamaño sin JSONL.
/// </summary>
public sealed class VlmHubLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly string _preferredDirectory;
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private string? _currentDate;
    private string? _currentPath;
    private bool _disposed;

    public VlmHubLogger(string directory, long maxBytes = 20L * 1024 * 1024)
    {
        _preferredDirectory = directory;
        _maxBytes = Math.Max(1024 * 1024, maxBytes);
    }

    public string CurrentLogPath
    {
        get
        {
            lock (_sync)
            {
                EnsureWriter(DateTimeOffset.Now);
                return _currentPath!;
            }
        }
    }

    public void Info(
        string module,
        string eventName,
        string message,
        Guid? processingId = null,
        Guid? itemId = null,
        string? primaryKey = null,
        string? server = null,
        string? model = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("INFO", module, eventName, message, processingId, itemId, primaryKey, server, model, data, null);

    public void Warning(
        string module,
        string eventName,
        string message,
        Guid? processingId = null,
        Guid? itemId = null,
        string? primaryKey = null,
        string? server = null,
        string? model = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("WARN", module, eventName, message, processingId, itemId, primaryKey, server, model, data, null);

    public void Error(
        string module,
        string eventName,
        string message,
        Exception? exception = null,
        Guid? processingId = null,
        Guid? itemId = null,
        string? primaryKey = null,
        string? server = null,
        string? model = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Write("ERROR", module, eventName, message, processingId, itemId, primaryKey, server, model, data, exception);

    internal void WriteBalancer(
        string level,
        string eventName,
        string detail,
        Guid? processingId,
        Guid? itemId,
        string? server,
        string? model) =>
        Write(level, "BALANCER", eventName, detail, processingId, itemId, null, server, model, null, null);

    private void Write(
        string level,
        string module,
        string eventName,
        string message,
        Guid? processingId,
        Guid? itemId,
        string? primaryKey,
        string? server,
        string? model,
        IReadOnlyDictionary<string, object?>? data,
        Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                var now = DateTimeOffset.Now;
                EnsureWriter(now);
                RotateBySizeIfNeeded(now);

                var context = new List<string>(8)
                {
                    $"event={Compact(eventName)}"
                };
                if (processingId is { } processing) context.Add($"processing={processing:N}");
                if (itemId is { } item) context.Add($"item={item:N}");
                if (!string.IsNullOrWhiteSpace(primaryKey)) context.Add($"pk={Compact(primaryKey)}");
                if (!string.IsNullOrWhiteSpace(server)) context.Add($"server={Compact(server)}");
                if (!string.IsNullOrWhiteSpace(model)) context.Add($"model={Compact(model)}");

                if (data is not null)
                {
                    foreach (var pair in data)
                    {
                        if (pair.Value is null) continue;
                        context.Add($"{Compact(pair.Key)}={Compact(FormatValue(pair.Value))}");
                    }
                }

                var error = exception is null
                    ? string.Empty
                    : $" | {exception.GetType().Name}: {Compact(exception.Message)}";
                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} | {level.PadRight(5)} | {module.PadRight(10)} | {string.Join(" ", context)} | {Compact(message)}{error}";
                _writer!.WriteLine(line);
            }
        }
        catch
        {
            // El diagnóstico jamás detiene procesamiento o persistencia.
        }
    }

    private void EnsureWriter(DateTimeOffset now)
    {
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == date)
        {
            return;
        }

        _writer?.Dispose();
        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);
        _currentDate = date;
        _currentPath = Path.Combine(directory, $"vlmhub_{date}.log");
        RotateExistingIfOversized(_currentPath);
        _writer = CreateWriter(_currentPath);
    }

    private void RotateBySizeIfNeeded(DateTimeOffset now)
    {
        if (_currentPath is null || _writer is null)
        {
            return;
        }

        try
        {
            if (_writer.BaseStream.Length < _maxBytes)
            {
                return;
            }

            _writer.Dispose();
            _writer = null;
            RotateFile(_currentPath);
            _writer = CreateWriter(_currentPath);
            _currentDate = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }
        catch
        {
            _writer ??= CreateWriter(_currentPath);
        }
    }

    private void RotateExistingIfOversized(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length >= _maxBytes)
            {
                RotateFile(path);
            }
        }
        catch
        {
            // Se intentará abrir el archivo base normalmente.
        }
    }

    private static void RotateFile(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index:000}.log");
            if (File.Exists(candidate)) continue;
            File.Move(path, candidate);
            return;
        }
    }

    private string ResolveDirectory()
    {
        try
        {
            Directory.CreateDirectory(_preferredDirectory);
            return _preferredDirectory;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "VlmHub", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    private static string FormatValue(object value) => value switch
    {
        string text => text,
        IEnumerable<string> strings => string.Join(",", strings),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string Compact(string value)
    {
        var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 320 ? line : line[..317] + "...";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
