using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Frameworks;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using XamlG.Tooling;

namespace XamlG.Workspaces;

/// <summary>Adapts any Roslyn Project, including AdhocWorkspace projects. MSBuild is not required by this entry point.</summary>
public static class XamlWorkspaceProjectLoader
{
    private static readonly ImmutableHashSet<string> OwnedGenerators = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "XamlG.Generator.dll", "Avalonia.Generators.dll");

    public static async Task<XamlWorkspaceProject> LoadAsync(Project project, XamlWorkspaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new();
        if (project.Language != LanguageNames.CSharp) throw new NotSupportedException("The XamlG C# backend requires a C# Roslyn project.");
        var generators = options.RunApplicationSourceGenerators
            ? project.AnalyzerReferences.Where(reference => !OwnedGenerators.Contains(Path.GetFileName(reference.FullPath ?? string.Empty)))
            : Enumerable.Empty<Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference>();
        project = project.WithAnalyzerReferences(generators);
        var compilation = await project.GetCompilationAsync(cancellationToken) as CSharpCompilation
            ?? throw new InvalidOperationException("Roslyn did not provide a C# compilation for this project.");
        var defaults = options.CompileBindingsByDefault ?? AvaloniaBuildOptions.ReadCompileBindingsByDefault(
            project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions);
        var profile = KnownFrameworkProfiles.Select(compilation, options.Framework, defaults);
        var documents = ImmutableDictionary.CreateBuilder<string, XamlSyntaxTree>(StringComparer.Ordinal);
        var root = Path.GetDirectoryName(project.FilePath);
        foreach (var document in project.AdditionalDocuments)
        {
            var path = document.FilePath ?? document.Name;
            if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;
            var logicalPath = root != null && Path.IsPathFullyQualified(path) ? Path.GetRelativePath(root, path) : path;
            var source = await document.GetTextAsync(cancellationToken);
            documents[path] = XamlSyntaxTree.Parse(source.ToString(), logicalPath.Replace('\\', '/'), cancellationToken);
        }
        var compiler = new XamlCompilationSession(compilation, profile, new XamlCompilerOptions());
        return new(project, compiler, documents.ToImmutable());
    }
}
