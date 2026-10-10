using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using XamlG.ProjectSystem;

namespace XamlG.Workspaces.Studio;

public sealed record WorkspaceEvaluationRequest(string Path, bool Trust = false, string Configuration = "Debug", string Platform = "AnyCPU",
    string? Framework = null, bool IncludeCompilerDiagnostics = false);
public sealed record EvaluatedDocument(string Id, string Name, string? Path, string Kind, ImmutableArray<string> Folders, bool External);
public sealed record EvaluatedDiagnostic(string Id, string Severity, string Message, string? Path, int Line, int Column);
public sealed record EvaluatedProject(string Id, string Name, string Language, string? Path, string? AssemblyName,
    string? OutputPath, string? TargetFramework, string? LanguageVersion, ImmutableArray<string> PreprocessorSymbols,
    ImmutableArray<string> ProjectReferences, ImmutableArray<string> MetadataReferences,
    ImmutableArray<string> AnalyzerReferences, ImmutableArray<EvaluatedDocument> Documents);
public sealed record EvaluatedWorkspace(string EntryPath, bool Evaluated, ImmutableArray<EvaluatedProject> Projects,
    ImmutableArray<string> WorkspaceDiagnostics, ImmutableArray<EvaluatedDiagnostic> CompilerDiagnostics);

/// <summary>Loads actual SDK design-time builds. Opening a snapshot is a trusted execution operation.</summary>
public static class StudioWorkspaceEvaluator
{
    public static Task<EvaluatedWorkspace> EvaluateAsync(WorkspaceFileSystem files, WorkspaceEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files); ArgumentNullException.ThrowIfNull(request);
        if (!request.Trust) throw new InvalidOperationException("MSBuild evaluation can execute SDK, project and package code. Explicit workspace trust is required.");
        var path = files.Resolve(request.Path);
        if (!WorkspacePath.IsEntry(path) || !File.Exists(path)) throw new ArgumentException("Choose an existing solution or project.");
        ValidateBuildValue(request.Configuration); ValidateBuildValue(request.Platform);
        if (request.Framework != null) ValidateBuildValue(request.Framework);
        cancellationToken.ThrowIfCancellationRequested();
        MSBuildBootstrapper.EnsureRegistered();
        return EvaluateCoreAsync(files, request, path, cancellationToken);
    }

    public static void ValidateBuildValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.' or ' ')))
            throw new ArgumentException("Invalid build configuration, platform or framework.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<EvaluatedWorkspace> EvaluateCoreAsync(WorkspaceFileSystem files, WorkspaceEvaluationRequest request, string path, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = request.Configuration, ["Platform"] = request.Platform,
            ["DesignTimeBuild"] = "true", ["BuildingInsideVisualStudio"] = "true",
            ["SkipCompilerExecution"] = "true", ["ProvideCommandLineArgs"] = "true"
        };
        if (request.Framework != null) properties["TargetFramework"] = request.Framework;
        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.LoadMetadataForReferencedProjects = false;
        workspace.SkipUnrecognizedProjects = true;
        var failures = new ConcurrentQueue<string>();
        using var registration = workspace.RegisterWorkspaceFailedHandler(args =>
        {
            if (failures.Count < 2000) failures.Enqueue(args.Diagnostic.Kind + ": " + args.Diagnostic.Message);
        });
        Solution solution;
        if (WorkspacePath.IsProject(path))
        {
            var project = await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            solution = project.Solution;
        }
        else solution = await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        var projects = ImmutableArray.CreateBuilder<EvaluatedProject>();
        var diagnostics = ImmutableArray.CreateBuilder<EvaluatedDiagnostic>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projects.Count >= VirtualWorkspace.MaximumFiles) throw new InvalidOperationException("Evaluated project limit exceeded.");
            var documents = ImmutableArray.CreateBuilder<EvaluatedDocument>();
            void AddDocuments(IEnumerable<TextDocument> source, string kind)
            {
                foreach (var document in source)
                {
                    if (documents.Count >= VirtualWorkspace.MaximumFiles) throw new InvalidOperationException("Evaluated document limit exceeded.");
                    var relative = PortablePath(files, document.FilePath);
                    documents.Add(new(document.Id.Id.ToString(), document.Name, relative ?? document.FilePath, kind,
                        document.Folders.ToImmutableArray(), document.FilePath != null && relative == null));
                }
            }
            AddDocuments(project.Documents, "Compile");
            AddDocuments(project.AdditionalDocuments, "AdditionalFile");
            AddDocuments(project.AnalyzerConfigDocuments, "AnalyzerConfig");
            project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.TargetFramework", out var targetFramework);
            var csharp = project.ParseOptions as CSharpParseOptions;
            projects.Add(new(project.Id.Id.ToString(), project.Name, project.Language, PortablePath(files, project.FilePath) ?? project.FilePath,
                project.AssemblyName, project.OutputFilePath, targetFramework, csharp?.LanguageVersion.ToString(),
                csharp?.PreprocessorSymbolNames.ToImmutableArray() ?? [],
                project.ProjectReferences.Select(reference => reference.ProjectId.Id.ToString()).ToImmutableArray(),
                project.MetadataReferences.Select(reference => reference.Display ?? "<in-memory reference>").ToImmutableArray(),
                project.AnalyzerReferences.Select(reference => reference.FullPath ?? reference.Display ?? "<in-memory analyzer>").ToImmutableArray(),
                documents.ToImmutable()));
            if (request.IncludeCompilerDiagnostics && diagnostics.Count < 2000 && project.SupportsCompilation)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation == null) continue;
                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken).Take(2000 - diagnostics.Count))
                {
                    var line = diagnostic.Location.GetLineSpan();
                    diagnostics.Add(new(diagnostic.Id, diagnostic.Severity.ToString(), diagnostic.GetMessage(),
                        PortablePath(files, line.Path) ?? line.Path, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1));
                }
            }
        }
        return new(WorkspacePath.Normalize(request.Path), true, projects.ToImmutable(), failures.ToImmutableArray(), diagnostics.ToImmutable());
    }

    private static string? PortablePath(WorkspaceFileSystem files, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return files.Relative(path); }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
