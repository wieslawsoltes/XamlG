using XamlG.LanguageServer;
using XamlG.Lsp;
using XamlG.Workspaces.Watching;

using var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
try
{
    var options = ServerOptions.Parse(args);
    if (options.Help)
    {
        Console.Error.WriteLine("""
            XamlG language server — stdio transport

            xamlg-lsp --code Model.cs --reference Controls.dll --framework Portable
            xamlg-lsp --project App.csproj --trust-project --target-framework net10.0

            --code and --reference may be repeated. --framework defaults to Auto.
            Project evaluation and source generators may execute project code; --trust-project is mandatory.
            Compiler inputs are watched automatically. --no-watch disables automatic refresh.
            Failed or superseded refreshes retain the last published project snapshot.
            Stdout is reserved for framed JSON-RPC; logs are written to stderr.
            """);
        return 0;
    }
    var loader = new ServerCompilationLoader(options);
    var initial = await loader.LoadAsync(cancellation.Token);
    using var input = Console.OpenStandardInput();
    await using var server = new XamlLanguageServer(initial.Compiler, input, output, Console.Error);
    XamlFileMonitor? monitor = null;
    await using var refresh = new LatestRevisionWorker<ServerCompilationState>(
        (_, token) => loader.LoadAsync(token),
        result =>
        {
            monitor?.Update(result.Value.Inputs);
            server.UpdateCompilation(result.Value.Compiler);
            Console.Error.WriteLine("workspace refreshed: " + result.Revision);
        }, error => Console.Error.WriteLine("workspace refresh retained the previous snapshot: " + error.Message));
    try
    {
        if (options.Watch) monitor = new(initial.Inputs, refresh.Signal, error => Console.Error.WriteLine("workspace watcher: " + error.Message));
        await server.RunAsync(cancellation.Token);
    }
    finally { monitor?.Dispose(); }
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
catch (Exception error) { await Console.Error.WriteLineAsync("xamlg-lsp: " + error.Message); return 2; }
