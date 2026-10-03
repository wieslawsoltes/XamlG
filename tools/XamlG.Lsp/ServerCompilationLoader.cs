using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Frameworks;
using XamlG.Workspaces;
using XamlG.Workspaces.Watching;

namespace XamlG.Lsp;

internal sealed class ServerCompilationLoader(ServerOptions options)
{
    public async Task<ServerCompilationState> LoadAsync(CancellationToken cancellationToken)
    {
        if (options.Project != null)
        {
            var properties = ImmutableDictionary<string, string>.Empty;
            if (options.TargetFramework != null) properties = properties.Add("TargetFramework", options.TargetFramework);
            using var workspace = XamlWorkspaceHost.Create(new()
            { AllowProjectEvaluation = options.TrustProject, Framework = options.Framework, GlobalProperties = properties });
            var project = await workspace.OpenProjectAsync(options.Project, cancellationToken);
            var failures = workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure).ToArray();
            if (failures.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(d => d.Message)));
            foreach (var diagnostic in workspace.Diagnostics) await Console.Error.WriteLineAsync("workspace: " + diagnostic.Message);
            return new(project.Compiler, XamlWatchInputs.FromProject(project.Project));
        }
        var platformPaths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var references = platformPaths.Concat(options.References.Select(Path.GetFullPath)).Distinct(XamlWatchInputs.PathComparer)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var syntax = new List<SyntaxTree>();
        foreach (var path in options.CodeFiles)
            syntax.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path, cancellationToken),
                new CSharpParseOptions(LanguageVersion.Preview), Path.GetFullPath(path), cancellationToken: cancellationToken));
        var compilation = CSharpCompilation.Create("XamlG.LanguageServer.Project", syntax, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return new(new(compilation, KnownFrameworkProfiles.Select(compilation, options.Framework)),
            new XamlWatchInputs(options.CodeFiles.Concat(options.References)));
    }
}
