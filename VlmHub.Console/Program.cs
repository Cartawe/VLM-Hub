using VlmHub.Balancer.Logging;
using VlmHub.Console.App;
using VlmHub.Console.Processing;
using VlmHub.Console.Views;

var commandLineArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
var isWorker = commandLineArgs.Length >= 2 &&
               string.Equals(commandLineArgs[0], "--worker", StringComparison.OrdinalIgnoreCase);

using var cancellationSource = new CancellationTokenSource();
using var logger = new VlmHubLogger(Path.Combine(Environment.CurrentDirectory, "logs"));

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

if (isWorker)
{
    try
    {
        Environment.ExitCode = await ProcessingWorker.RunAsync(
            commandLineArgs[1],
            logger,
            cancellationSource.Token);
    }
    catch (Exception exception)
    {
        logger.Error(
            "WORKER",
            "worker.bootstrap_failed",
            "El worker no pudo inicializarse.",
            exception);
        Environment.ExitCode = 1;
    }

    return;
}

try
{
    logger.Info("CONSOLE", "application.started", "VLMHub iniciado.");
    var app = new VlmHubConsoleApp(logger);
    await app.RunAsync(cancellationSource.Token);
    logger.Info("CONSOLE", "application.stopped", "VLMHub finalizado.");
}
catch (OperationCanceledException)
{
    logger.Warning("CONSOLE", "application.cancelled", "VLMHub fue detenido por el usuario.");
    ConsoleUi.ShowWarning("VLMHub fue detenido por el usuario.");
}
catch (Exception exception)
{
    logger.Error("CONSOLE", "application.fatal_error", "Error no controlado en el proceso principal.", exception);
    ConsoleUi.ShowError(exception);
}
