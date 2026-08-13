namespace VlmHub.Console.Processing;

internal sealed class ProcessingWorkspace
{
    public Guid ProcessingId { get; }
    public string RootDirectory { get; }
    public string ControlDirectory { get; }
    public string ItemsDirectory { get; }
    public string ResultsDirectory { get; }
    public string TempDirectory { get; }
    public string RunPath => Path.Combine(ControlDirectory, "run.json");
    public string SelectionPath => Path.Combine(ControlDirectory, "selection.json");
    public string CancelRequestPath => Path.Combine(ControlDirectory, "cancel.requested");
    public string WorkerLockPath => Path.Combine(ControlDirectory, "worker.lock");
    public string WorkerPidPath => Path.Combine(ControlDirectory, "worker.pid");

    private ProcessingWorkspace(Guid processingId, string rootDirectory)
    {
        ProcessingId = processingId;
        RootDirectory = rootDirectory;
        ControlDirectory = Path.Combine(rootDirectory, "control");
        ItemsDirectory = Path.Combine(ControlDirectory, "items");
        ResultsDirectory = Path.Combine(rootDirectory, "results");
        TempDirectory = Path.Combine(rootDirectory, "temp");
    }

    public static ProcessingWorkspace Create(string processingRoot)
    {
        var id = Guid.NewGuid();
        var folder = $"{DateTimeOffset.Now:yyyyMMdd_HHmmss}_{id.ToString("N")[..8]}";
        var workspace = new ProcessingWorkspace(id, Path.Combine(processingRoot, folder));
        workspace.EnsureDirectories();
        return workspace;
    }

    public static ProcessingWorkspace Open(Guid processingId, string rootDirectory)
    {
        var workspace = new ProcessingWorkspace(processingId, rootDirectory);
        workspace.EnsureDirectories();
        return workspace;
    }

    public string ItemControlPath(Guid itemId) =>
        Path.Combine(ItemsDirectory, $"{itemId:N}.json");

    public string ResultPath(Guid itemId) =>
        Path.Combine(ResultsDirectory, $"{itemId:N}.json");

    public string TempBasePath(Guid itemId) =>
        Path.Combine(TempDirectory, itemId.ToString("N"));

    public async Task InitializeItemControlsAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in SelectionManifestReader.ReadAsync(SelectionPath, cancellationToken))
        {
            var path = ItemControlPath(item.ItemId);
            if (File.Exists(path))
            {
                continue;
            }

            await AtomicJsonFile.WriteAsync(
                path,
                new ProcessingItemControl
                {
                    ItemId = item.ItemId,
                    State = ProcessingStates.Pending,
                    UpdatedAt = DateTimeOffset.Now
                },
                cancellationToken);
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(ControlDirectory);
        Directory.CreateDirectory(ItemsDirectory);
        Directory.CreateDirectory(ResultsDirectory);
        Directory.CreateDirectory(TempDirectory);
    }
}
