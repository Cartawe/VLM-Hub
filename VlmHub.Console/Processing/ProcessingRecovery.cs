using VlmHub.Balancer.Models;

namespace VlmHub.Console.Processing;

internal static class ProcessingRecovery
{
    public static async Task<IReadOnlyList<ProcessingRunSummary>> FindIncompleteRunsAsync(
        string processingRoot,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(processingRoot))
        {
            return Array.Empty<ProcessingRunSummary>();
        }

        var runs = new List<ProcessingRunSummary>();

        foreach (var directory in Directory.EnumerateDirectories(processingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runPath = Path.Combine(directory, "control", "run.json");
            var selectionPath = Path.Combine(directory, "control", "selection.json");
            if (!File.Exists(runPath) || !File.Exists(selectionPath))
            {
                continue;
            }

            try
            {
                var run = await AtomicJsonFile.ReadAsync<ProcessingRunControl>(runPath, cancellationToken);
                if (run is null || string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var completed = await CountCompletedAsync(
                    Path.Combine(directory, "control", "items"),
                    cancellationToken);
                runs.Add(new ProcessingRunSummary(
                    directory,
                    run,
                    completed,
                    Math.Max(0, run.Total - completed)));
            }
            catch
            {
                // Un run.json ilegible no debe impedir recuperar las demás
                // ejecuciones. El archivo roto permanece disponible para diagnóstico.
            }
        }

        return runs
            .OrderByDescending(item => item.Run.CreatedAt)
            .ToArray();
    }

    public static async Task ReconcileAsync(
        ProcessingWorkspace workspace,
        CancellationToken cancellationToken)
    {
        AtomicJsonFile.DeleteOrphanTemps(workspace.RootDirectory);

        await foreach (var item in SelectionManifestReader.ReadAsync(
                           workspace.SelectionPath,
                           cancellationToken))
        {
            var path = workspace.ItemControlPath(item.ItemId);
            ProcessingItemControl control;
            var mustWrite = false;

            if (File.Exists(path))
            {
                control = await AtomicJsonFile.ReadAsync<ProcessingItemControl>(path, cancellationToken)
                          ?? NewPending(item.ItemId);
                if (control.ItemId != item.ItemId)
                {
                    control = NewPending(item.ItemId);
                    mustWrite = true;
                }
            }
            else
            {
                control = NewPending(item.ItemId);
                mustWrite = true;
            }

            if (control.State == ProcessingStates.Processing)
            {
                var resultPath = workspace.ResultPath(item.ItemId);
                var markerPath = $"{resultPath}.ready";
                var marker = File.Exists(markerPath) && File.Exists(resultPath)
                    ? await TryReadCompletionMarkerAsync(markerPath, cancellationToken)
                    : null;

                if (marker is not null)
                {
                    // El VLM ya terminó y el snapshot final está marcado como
                    // durable. Se conserva y solo queda reconstruir persistencia.
                    control.State = ProcessingStates.PendingUpload;
                    control.Success = marker.Success;
                    control.NumPages = marker.NumPages;
                    control.ProcessingTime = marker.ProcessingTime;
                    control.ResultPath = Path.GetRelativePath(
                            workspace.RootDirectory,
                            resultPath)
                        .Replace('\\', '/');
                    control.ServerProcessingId = null;
                    control.LastError = marker.Error;
                    control.UpdatedAt = marker.CompletedAt;
                }
                else
                {
                    control.State = ProcessingStates.Pending;
                    control.Success = null;
                    control.ServerProcessingId = null;
                    control.LastError = null;
                    control.ProcessingTime = null;
                    control.UpdatedAt = DateTimeOffset.Now;
                }

                mustWrite = true;
            }

            if (mustWrite)
            {
                await AtomicJsonFile.WriteAsync(path, control, cancellationToken);
            }
        }
    }

    private static async Task<ResultCompletionMarker?> TryReadCompletionMarkerAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await System.Text.Json.JsonSerializer.DeserializeAsync<ResultCompletionMarker>(
                stream,
                cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static ProcessingItemControl NewPending(Guid itemId) => new()
    {
        ItemId = itemId,
        State = ProcessingStates.Pending,
        UpdatedAt = DateTimeOffset.Now
    };

    private static async Task<long> CountCompletedAsync(
        string itemsDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(itemsDirectory))
        {
            return 0;
        }

        long completed = 0;
        foreach (var path in Directory.EnumerateFiles(itemsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var control = await AtomicJsonFile.ReadAsync<ProcessingItemControl>(path, cancellationToken);
                if (control?.State == ProcessingStates.Completed)
                {
                    completed++;
                }
            }
            catch
            {
                // Un control ilegible se considera pendiente y podrá regenerarse
                // al recorrer selection.json.
            }
        }

        return completed;
    }
}
