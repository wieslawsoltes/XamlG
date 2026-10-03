using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Frameworks;
using XamlG.LanguageServer;
using XamlG.Lsp;
using XamlG.Tooling;
using XamlG.Workspaces;

// Reserve stdout for framed JSON-RPC before any project-defined code can run.
using var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
XamlWorkspaceHost? workspace = null;
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
            Project evaluation and application source generators execute project code:
            --trust-project is required for --project. No project is evaluated implicitly.
            Metadata-only mode reads C# and assemblies without executing their code.
            Diagnostics and host logs are written to stderr; stdout is JSON-RPC only.
            """);
        return 0;
    }

    XamlCompilationSession compiler;
    if (options.Project != null)
    {
        var properties = ImmutableDictionary<string, string>.Empty;
        if (options.TargetFramework != null) properties = properties.Add("TargetFramework", options.TargetFramework);
        workspace = XamlWorkspaceHost.Create(new()
        {
            AllowProjectEvaluation = options.TrustProject,
            Framework = options.Framework,
            GlobalProperties = properties
        });
        var project = await workspace.OpenProjectAsync(options.Project, cancellation.Token);
        foreach (var diagnostic in workspace.Diagnostics) await Console.Error.WriteLineAsync("workspace: " + diagnostic.Message);
        compiler = project.Compiler;
    }
    else
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(options.References.Select(Path.GetFullPath));
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var references = paths.Distinct(pathComparer).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var syntax = new List<SyntaxTree>();
        foreach (var path in options.CodeFiles)
            syntax.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path, cancellation.Token),
                new CSharpParseOptions(LanguageVersion.Preview), Path.GetFullPath(path), cancellationToken: cancellation.Token));
        var compilation = CSharpCompilation.Create("XamlG.LanguageServer.Project", syntax, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        compiler = new(compilation, KnownFrameworkProfiles.Select(compilation, options.Framework));
    }

    using var input = Console.OpenStandardInput();
    await using var server = new XamlLanguageServer(compiler, input, output, Console.Error);
    await server.RunAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
catch (Exception error) { await Console.Error.WriteLineAsync("xamlg-lsp: " + error.Message); return 2; }
finally { workspace?.Dispose(); }
