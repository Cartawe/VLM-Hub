using VlmHub.Console.App;
using VlmHub.Console.Views;

using var cancellationSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

try
{
    var app = new VlmHubConsoleApp();
    await app.RunAsync(cancellationSource.Token);
}
catch (OperationCanceledException)
{
    ConsoleUi.ShowWarning("VLMHub fue detenido por el usuario.");
}
catch (Exception exception)
{
    // Última barrera de seguridad: un error no manejado se presenta de forma
    // controlada en vez de imprimir un stack trace crudo en la consola.
    ConsoleUi.ShowError(exception);
}
