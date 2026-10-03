using XamlG.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
try { return await new CompilerCommand().RunAsync(CliOptions.Parse(args), cancellation.Token); }
catch (OperationCanceledException) { return 130; }
catch (Exception error) { Console.Error.WriteLine("xamlg: " + error.Message); return 2; }
