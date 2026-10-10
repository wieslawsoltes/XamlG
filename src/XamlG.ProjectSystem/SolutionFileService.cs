using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace XamlG.ProjectSystem;

public sealed record SolutionConfigurations(ImmutableArray<string> BuildTypes, ImmutableArray<string> Platforms);

/// <summary>
/// Browser-safe solution manipulation using Microsoft's shared solution model. Stream-only APIs
/// never evaluate projects, access their filesystem paths, or invoke MSBuild.
/// </summary>
public static class SolutionFileService
{
    public static async Task<SolutionConfigurations> ConfigurationsAsync(WorkspaceSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        var model = await LoadAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        return new(model.BuildTypes.ToImmutableArray(), model.Platforms.ToImmutableArray());
    }

    public static Task<WorkspaceChange> AddProjectAsync(WorkspaceSnapshot snapshot, string solutionPath, string projectPath, string? folder = null, CancellationToken cancellationToken = default)
    {
        projectPath = WorkspacePath.Normalize(projectPath);
        if (!WorkspacePath.IsProject(projectPath)) throw new ArgumentException("Choose a project file.");
        var relative = WorkspacePath.RelativeTo(solutionPath, projectPath);
        return EditAsync(snapshot, solutionPath, model =>
        {
            if (model.SolutionProjects.Any(project => WorkspacePath.Resolve(solutionPath, project.FilePath) == projectPath))
                throw new InvalidOperationException("The solution already contains this project.");
            model.AddProject(relative, folder: folder == null ? null : model.AddFolder(FolderPath(folder)));
        }, cancellationToken);
    }

    public static Task<WorkspaceChange> RemoveProjectAsync(WorkspaceSnapshot snapshot, string solutionPath, string projectPath, CancellationToken cancellationToken = default)
    {
        projectPath = WorkspacePath.Normalize(projectPath);
        return EditAsync(snapshot, solutionPath, model =>
        {
            var project = model.SolutionProjects.SingleOrDefault(project => WorkspacePath.Resolve(solutionPath, project.FilePath) == projectPath)
                ?? throw new ArgumentException("The project is not a member of this solution.");
            model.RemoveProject(project);
        }, cancellationToken);
    }

    public static Task<WorkspaceChange> AddFolderAsync(WorkspaceSnapshot snapshot, string path, string folder, CancellationToken cancellationToken = default) =>
        EditAsync(snapshot, path, model => model.AddFolder(FolderPath(folder)), cancellationToken);

    public static Task<WorkspaceChange> RemoveFolderAsync(WorkspaceSnapshot snapshot, string path, string folder, CancellationToken cancellationToken = default) =>
        EditAsync(snapshot, path, model =>
        {
            var existing = model.FindFolder(FolderPath(folder)) ?? throw new ArgumentException("The solution folder does not exist.");
            model.RemoveFolder(existing);
        }, cancellationToken);

    public static Task<WorkspaceChange> MoveProjectAsync(WorkspaceSnapshot snapshot, string path, string projectPath, string? folder, CancellationToken cancellationToken = default) =>
        EditAsync(snapshot, path, model =>
        {
            var project = model.SolutionProjects.SingleOrDefault(project => WorkspacePath.Resolve(path, project.FilePath) == WorkspacePath.Normalize(projectPath))
                ?? throw new ArgumentException("The project is not a member of this solution.");
            project.MoveToFolder(folder == null ? null : model.AddFolder(FolderPath(folder)));
        }, cancellationToken);

    public static Task<WorkspaceChange> AddSolutionItemAsync(WorkspaceSnapshot snapshot, string path, string filePath, string folder = "Solution Items", CancellationToken cancellationToken = default)
    {
        filePath = WorkspacePath.Normalize(filePath);
        if (!snapshot.Files.ContainsKey(filePath)) throw new ArgumentException("Choose an existing workspace file.");
        return EditAsync(snapshot, path, model => model.AddFolder(FolderPath(folder)).AddFile(WorkspacePath.RelativeTo(path, filePath)), cancellationToken);
    }

    public static Task<WorkspaceChange> AddConfigurationAsync(WorkspaceSnapshot snapshot, string path, string configuration, string platform, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration); ValidateConfiguration(platform);
        return EditAsync(snapshot, path, model => { model.AddBuildType(configuration); model.AddPlatform(platform); }, cancellationToken);
    }

    public static async Task<WorkspaceChange> ConvertAsync(WorkspaceSnapshot snapshot, string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        destinationPath = WorkspacePath.Normalize(destinationPath);
        RequireFormat(destinationPath);
        if (snapshot.Files.ContainsKey(destinationPath)) throw new InvalidOperationException("Solution conversion never overwrites an existing file.");
        // Relocation would require rewriting all relative items and configuration references. Conversion
        // intentionally changes only the format beside the source, leaving the original available.
        if (WorkspacePath.Directory(sourcePath) != WorkspacePath.Directory(destinationPath)) throw new ArgumentException("Convert beside the original solution.");
        var model = await LoadAsync(snapshot, sourcePath, cancellationToken).ConfigureAwait(false);
        model.SerializerExtension = Path.GetExtension(destinationPath).Equals(".sln", StringComparison.OrdinalIgnoreCase)
            ? SolutionSerializers.SlnFileV12.CreateModelExtension() : SolutionSerializers.SlnXml.CreateModelExtension();
        return new(destinationPath, null, new(await SaveAsync(model, destinationPath, cancellationToken).ConfigureAwait(false)));
    }

    public static async Task<WorkspaceTemplatePlan> CreateAsync(string name, string format = "slnx", ProjectTemplateRequest? initialProject = null, CancellationToken cancellationToken = default)
    {
        WorkspacePath.ValidateName(name);
        if (format is not ("sln" or "slnx")) throw new ArgumentException("Choose sln or slnx.");
        var files = ImmutableDictionary.CreateBuilder<string, WorkspaceFile>(StringComparer.Ordinal);
        var model = new SolutionModel();
        model.AddBuildType("Debug"); model.AddBuildType("Release"); model.AddPlatform("Any CPU");
        if (initialProject != null)
        {
            var project = WorkspaceTemplates.CreateProject(initialProject);
            foreach (var pair in project.Files) files.Add(pair.Key, pair.Value);
            model.AddProject(project.EntryPath);
        }
        var path = name + "." + format;
        files.Add(path, new(await SaveAsync(model, path, cancellationToken).ConfigureAwait(false)));
        var plan = new WorkspaceTemplatePlan(path, files.ToImmutable());
        _ = plan.CreateWorkspace();
        return plan;
    }

    private static async Task<WorkspaceChange> EditAsync(WorkspaceSnapshot snapshot, string path, Action<SolutionModel> edit, CancellationToken cancellationToken)
    {
        path = WorkspacePath.Normalize(path);
        var model = await LoadAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
        edit(model);
        return new(path, snapshot.Files[path], new(await SaveAsync(model, path, cancellationToken).ConfigureAwait(false)));
    }

    private static async Task<SolutionModel> LoadAsync(WorkspaceSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        path = WorkspacePath.Normalize(path); RequireFormat(path);
        var text = SolutionInspector.ReadText(snapshot, path);
        if (text.Length > VirtualWorkspace.MaximumFileCharacters) throw new ArgumentException("Solution file limit exceeded.");
        var isXml = Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase);
        if (isXml) _ = SolutionInspector.ReadXml(text, "Solution");
        // The virtual workspace stores decoded text. Ignore stale encoding declarations while making
        // a UTF-8 stream for the shared serializer; persisted bytes are chosen by the file adapter.
        if (isXml)
        {
            var xml = SolutionInspector.ReadXml(text, "Solution");
            if (xml.Declaration != null) xml.Declaration.Encoding = "utf-8";
            text = xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
        var model = isXml
            ? await SolutionSerializers.SlnXml.OpenAsync(stream, cancellationToken).ConfigureAwait(false)
            : await SolutionSerializers.SlnFileV12.OpenAsync(stream, cancellationToken).ConfigureAwait(false);
        if (model.SolutionItems.Count > VirtualWorkspace.MaximumFiles) throw new ArgumentException("Solution item limit exceeded.");
        return model;
    }

    private static async Task<string> SaveAsync(SolutionModel model, string path, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        if (Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            await SolutionSerializers.SlnXml.SaveAsync(stream, model, cancellationToken).ConfigureAwait(false);
        else await SolutionSerializers.SlnFileV12.SaveAsync(stream, model, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > VirtualWorkspace.MaximumFileCharacters) throw new ArgumentException("Serialized solution limit exceeded.");
        return text;
    }
    private static string FolderPath(string folder) => "/" + WorkspacePath.Normalize(folder.Trim('/')) + "/";
    private static void RequireFormat(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".sln" or ".slnx")) throw new ArgumentException("Choose a sln or slnx file; edit solution filters as JSON.");
    }
    private static void ValidateConfiguration(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ' ')))
            throw new ArgumentException("Invalid solution configuration or platform.");
    }
}
