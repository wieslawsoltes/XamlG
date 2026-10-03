using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace XamlG.Workspaces;

/// <summary>Trusted MSBuild evaluation boundary, isolated from compiler and browser assemblies.</summary>
public sealed class XamlWorkspaceHost : IDisposable
{
    private readonly MSBuildWorkspace _workspace;
    private readonly XamlWorkspaceOptions _options;
    private readonly ConcurrentQueue<WorkspaceDiagnostic> _diagnostics = new();
    private readonly WorkspaceEventRegistration _failureRegistration;

    private XamlWorkspaceHost(MSBuildWorkspace workspace, XamlWorkspaceOptions options)
    {
        _workspace = workspace;
        _options = options;
        _failureRegistration = _workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);
    }

    public ImmutableArray<WorkspaceDiagnostic> Diagnostics => _diagnostics.ToImmutableArray();

    public static XamlWorkspaceHost Create(XamlWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AllowProjectEvaluation)
            throw new InvalidOperationException("Evaluating an MSBuild project may execute project-defined code. Set AllowProjectEvaluation only for projects you trust.");
        MSBuildBootstrapper.EnsureRegistered();
        return CreateCore(options);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static XamlWorkspaceHost CreateCore(XamlWorkspaceOptions options)
    {
        var properties = new Dictionary<string, string>(options.GlobalProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true",
            ["BuildingInsideVisualStudio"] = "true",
            ["SkipCompilerExecution"] = "true",
            ["ProvideCommandLineArgs"] = "true"
        };
        return new(MSBuildWorkspace.Create(properties), options);
    }

    public async Task<XamlWorkspaceProject> OpenProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var project = await _workspace.OpenProjectAsync(Path.GetFullPath(projectPath), cancellationToken: cancellationToken);
        return await XamlWorkspaceProjectLoader.LoadAsync(project, _options, cancellationToken);
    }

    public async Task<ImmutableArray<XamlWorkspaceProject>> OpenSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspace.OpenSolutionAsync(Path.GetFullPath(solutionPath), cancellationToken: cancellationToken);
        var result = ImmutableArray.CreateBuilder<XamlWorkspaceProject>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
            result.Add(await XamlWorkspaceProjectLoader.LoadAsync(project, _options, cancellationToken));
        return result.ToImmutable();
    }

    private void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs args) => _diagnostics.Enqueue(args.Diagnostic);
    public void Dispose()
    {
        _failureRegistration.Dispose();
        _workspace.Dispose();
    }
}
