using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
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
        var defaults = options.CompileBindingsByDefault ?? AvaloniaBuildOptions.ReadCompileBindingsByDefault(project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions);
        var profile = KnownFrameworkProfiles.Select(compilation, options.Framework, defaults);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var documents = ImmutableDictionary.CreateBuilder<string, XamlSyntaxTree>(pathComparer);
        var resources = new List<XamlProjectDocument>();
        var root = project.FilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(project.FilePath));
        foreach (var document in project.AdditionalDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = document.FilePath ?? document.Name;
            if (!path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.IsPathFullyQualified(path)) path = Path.GetFullPath(path);
            var source = (await document.GetTextAsync(cancellationToken)).ToString();
            // Framework targets and explicit AdditionalFiles can both contribute the same
            // physical input. Identical duplicates are one document, not duplicate exports.
            // Conflicting in-memory buffers must never become an order-dependent last writer.
            if (documents.TryGetValue(path, out var previous))
            {
                if (!StringComparer.Ordinal.Equals(previous.Text, source))
                    throw new InvalidOperationException("The project contains conflicting XAML AdditionalDocuments for '" + path + "'. Supply one consistent source buffer for each physical path.");
                continue;
            }
            var logicalPath = root != null && Path.IsPathFullyQualified(path) ? Path.GetRelativePath(root, path) : path;
            var syntax = XamlSyntaxTree.Parse(source, path, cancellationToken);
            documents.Add(path, syntax);
            resources.Add(new(syntax, logicalPath.Replace('\\', '/')));
        }
        var compiler = new XamlCompilationSession(compilation, profile, new XamlCompilerOptions(), projectDocuments: resources);
        return new(project, compiler, documents.ToImmutable());
    }
}
