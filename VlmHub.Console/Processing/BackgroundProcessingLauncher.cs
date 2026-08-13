using System.Diagnostics;
using System.Reflection;
using VlmHub.Balancer.Logging;

namespace VlmHub.Console.Processing;

/// <summary>
/// Lanza el mismo ejecutable en modo --worker y desacopla su vida de la
/// terminal interactiva. La terminal solo sigue el log y emite cancelación
/// explícita cuando recibe Ctrl+C.
/// </summary>
internal static class BackgroundProcessingLauncher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CooperativeCancelGrace = TimeSpan.FromSeconds(15);

    public static bool IsWorkerRunning(ProcessingWorkspace workspace)
    {
        if (!File.Exists(workspace.WorkerLockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(
                workspace.WorkerLockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static async Task<int> StartAndFollowAsync(
        ProcessingWorkspace workspace,
        string sqlHost,
        string sqlUser,
        string sqlPassword,
        VlmHubLogger logger,
        CancellationToken cancellationToken)
    {
        if (IsWorkerRunning(workspace))
        {
            throw new InvalidOperationException(
                "La ejecución ya tiene un worker activo; no se iniciará un duplicado.");
        }

        DeleteIfExists(workspace.CancelRequestPath);

        var tailer = new ProcessingLogTailer(
            Path.GetDirectoryName(logger.CurrentLogPath)
                ?? Path.Combine(Environment.CurrentDirectory, "logs"),
            workspace.ProcessingId,
            startAtEnd: true);

        using var process = StartDetachedWorker(
            workspace,
            sqlHost,
            sqlUser,
            sqlPassword);

        logger.Info(
            "CONSOLE",
            "worker.spawned",
            $"Worker iniciado con PID {process.Id}.",
            workspace.ProcessingId);

        try
        {
            while (!process.HasExited)
            {
                await tailer.PrintAvailableAsync(cancellationToken);
                await Task.Delay(PollInterval, cancellationToken);
            }

            await tailer.PrintAvailableAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelWorkerAsync(workspace, process, logger);
            throw;
        }
    }

    public static async Task FollowExistingAsync(
        ProcessingWorkspace workspace,
        VlmHubLogger logger,
        CancellationToken cancellationToken)
    {
        var tailer = new ProcessingLogTailer(
            Path.GetDirectoryName(logger.CurrentLogPath)
                ?? Path.Combine(Environment.CurrentDirectory, "logs"),
            workspace.ProcessingId,
            startAtEnd: true);

        try
        {
            while (IsWorkerRunning(workspace))
            {
                await tailer.PrintAvailableAsync(cancellationToken);
                await Task.Delay(PollInterval, cancellationToken);
            }

            await tailer.PrintAvailableAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RequestCancellation(workspace);
            await WaitForWorkerToStopAsync(workspace, logger);
            throw;
        }
    }

    private static Process StartDetachedWorker(
        ProcessingWorkspace workspace,
        string sqlHost,
        string sqlUser,
        string sqlPassword)
    {
        var (target, targetPrefixArguments) = ResolveEntryCommand();
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        if (OperatingSystem.IsLinux() && FindExecutable("setsid") is { } setsid)
        {
            startInfo.FileName = setsid;
            startInfo.ArgumentList.Add("--wait");
            startInfo.ArgumentList.Add(target);
            foreach (var argument in targetPrefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/nohup"))
        {
            startInfo.FileName = "/usr/bin/nohup";
            startInfo.ArgumentList.Add(target);
            foreach (var argument in targetPrefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            // En Windows CreateNoWindow evita asociar una nueva consola visible.
            // El worker no depende de stdin/stdout del proceso interactivo.
            startInfo.FileName = target;
            foreach (var argument in targetPrefixArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(workspace.RootDirectory);
        startInfo.Environment[ProcessingWorker.HostEnvironment] = sqlHost;
        startInfo.Environment[ProcessingWorker.UserEnvironment] = sqlUser;
        startInfo.Environment[ProcessingWorker.PasswordEnvironment] = sqlPassword;

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException("No fue posible iniciar el worker de VLMHub.");
    }

    private static (string Target, IReadOnlyList<string> PrefixArguments) ResolveEntryCommand()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("No fue posible resolver el ejecutable actual.");
        var fileName = Path.GetFileNameWithoutExtension(processPath);

        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new InvalidOperationException("No fue posible resolver VlmHub.Console.dll.");
            }

            return (processPath, new[] { assemblyPath });
        }

        return (processPath, Array.Empty<string>());
    }

    private static string? FindExecutable(string name)
    {
        var candidates = new[]
        {
            $"/usr/bin/{name}",
            $"/bin/{name}"
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task CancelWorkerAsync(
        ProcessingWorkspace workspace,
        Process process,
        VlmHubLogger logger)
    {
        RequestCancellation(workspace);
        logger.Warning(
            "CONSOLE",
            "worker.cancel_requested",
            "Ctrl+C solicitó la cancelación completa del worker.",
            workspace.ProcessingId);

        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var completed = await Task.WhenAny(
            exitTask,
            Task.Delay(CooperativeCancelGrace, CancellationToken.None));
        if (completed == exitTask)
        {
            return;
        }

        TryKill(process);
    }

    private static async Task WaitForWorkerToStopAsync(
        ProcessingWorkspace workspace,
        VlmHubLogger logger)
    {
        var deadline = DateTimeOffset.UtcNow + CooperativeCancelGrace;
        while (IsWorkerRunning(workspace) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, CancellationToken.None);
        }

        if (!IsWorkerRunning(workspace))
        {
            return;
        }

        if (TryReadWorkerPid(workspace) is { } pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                TryKill(process);
                logger.Warning(
                    "CONSOLE",
                    "worker.force_killed",
                    "El worker no respondió a la cancelación cooperativa y fue terminado.",
                    workspace.ProcessingId);
            }
            catch
            {
                // Puede haber terminado entre la comprobación y GetProcessById.
            }
        }
    }

    private static void RequestCancellation(ProcessingWorkspace workspace)
    {
        Directory.CreateDirectory(workspace.ControlDirectory);
        File.WriteAllText(
            workspace.CancelRequestPath,
            DateTimeOffset.UtcNow.ToString("O"));
    }

    private static int? TryReadWorkerPid(ProcessingWorkspace workspace)
    {
        try
        {
            return int.TryParse(File.ReadAllText(workspace.WorkerPidPath), out var pid)
                ? pid
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // La cancelación cooperativa ya pudo haber terminado el proceso.
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // El worker volverá a informar si la señal antigua sigue presente.
        }
    }

    private sealed class ProcessingLogTailer
    {
        private readonly string _logDirectory;
        private readonly string _processingToken;
        private string? _path;
        private long _position;

        public ProcessingLogTailer(string logDirectory, Guid processingId, bool startAtEnd)
        {
            _logDirectory = logDirectory;
            _processingToken = $"processing={processingId:N}";
            _path = CurrentLogPath();
            if (startAtEnd && _path is not null && File.Exists(_path))
            {
                _position = new FileInfo(_path).Length;
            }
        }

        public async Task PrintAvailableAsync(CancellationToken cancellationToken)
        {
            var currentPath = CurrentLogPath();
            if (currentPath is null || !File.Exists(currentPath))
            {
                return;
            }

            if (!string.Equals(_path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                _path = currentPath;
                _position = 0;
            }

            var length = new FileInfo(currentPath).Length;
            if (length < _position)
            {
                // Rotación por tamaño: el archivo base volvió a comenzar.
                _position = 0;
            }

            await using var stream = new FileStream(
                currentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (line.Contains(_processingToken, StringComparison.OrdinalIgnoreCase))
                {
                    global::System.Console.WriteLine(line);
                }
            }

            _position = stream.Position;
        }

        private string? CurrentLogPath()
        {
            var path = Path.Combine(
                _logDirectory,
                $"vlmhub_{DateTimeOffset.Now:yyyyMMdd}.log");
            return path;
        }
    }
}
