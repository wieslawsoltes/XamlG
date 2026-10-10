using System.Text.RegularExpressions;
using XamlG.ProjectSystem;

namespace XamlG.Workspaces.Studio;

public sealed record WorkspaceBuildRequest(string Path, string Operation, bool Trust = false, string Configuration = "Debug",
    string Platform = "AnyCPU", string? Framework = null);
public sealed record WorkspaceCreateRequest(string Name, string Template = "console", string? ParentDirectory = null,
    bool CreateSolution = true, string SolutionFormat = "slnx", string? Framework = null, string? Namespace = null,
    bool Trust = false, IReadOnlyDictionary<string, string>? TemplateOptions = null);
public sealed record WorkspaceCreateResult(string EntryPath, SdkCommandResult Command);
public sealed record WorkspaceSdkEditRequest(string Path, string Operation, string Value, bool Trust = false,
    string? Version = null, string? SolutionFolder = null, string? ExpectedHash = null);
public sealed record NativeTemplate(string Name, string ShortNames, string Language, string Type, string Tags, string Author);
public sealed record NativeTemplateCatalog(SdkCommandResult Command, IReadOnlyList<NativeTemplate> Templates);

/// <summary>Serialized owner SDK operations. All execution entry points explicitly require trust.</summary>
public sealed class StudioWorkspaceService
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ISdkProcessRunner _runner;
    public WorkspaceFileSystem Files { get; }

    public StudioWorkspaceService(string root, ISdkProcessRunner? runner = null)
    {
        Files = new(root);
        _runner = runner ?? new SdkProcessRunner();
    }

    public async Task<EvaluatedWorkspace> EvaluateAsync(WorkspaceEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        RequireTrust(request.Trust);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StudioWorkspaceEvaluator.EvaluateAsync(Files, request, cancellationToken).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    public async Task<SdkCommandResult> BuildAsync(WorkspaceBuildRequest request, CancellationToken cancellationToken = default)
    {
        RequireTrust(request.Trust);
        if (request.Operation is not ("restore" or "build" or "rebuild" or "clean" or "test")) throw new ArgumentException("Unsupported SDK build operation.");
        var path = ExistingEntry(request.Path);
        StudioWorkspaceEvaluator.ValidateBuildValue(request.Configuration);
        StudioWorkspaceEvaluator.ValidateBuildValue(request.Platform);
        if (request.Framework != null) StudioWorkspaceEvaluator.ValidateBuildValue(request.Framework);
        var args = new List<string> { request.Operation == "rebuild" ? "build" : request.Operation, path, "--nologo", "--verbosity", "minimal" };
        if (request.Operation == "rebuild") args.Add("--no-incremental");
        // MSBuild properties also work for restore, whose CLI has no --configuration option.
        args.Add("-p:Configuration=" + request.Configuration);
        args.Add("-p:Platform=" + request.Platform);
        if (request.Framework != null) args.Add("-p:TargetFramework=" + request.Framework);
        return await RunLockedAsync(Files.Root, args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NativeTemplateCatalog> TemplatesAsync(bool trust, CancellationToken cancellationToken = default)
    {
        RequireTrust(trust);
        var result = await RunLockedAsync(Files.Root, ["new", "list", "--columns-all"], cancellationToken).ConfigureAwait(false);
        return new(result, ParseTemplates(result.StandardOutput));
    }

    public Task<SdkCommandResult> TemplateHelpAsync(string template, bool trust, CancellationToken cancellationToken = default)
    {
        RequireTrust(trust); ValidateTemplate(template);
        return RunLockedAsync(Files.Root, ["new", template, "--help"], cancellationToken);
    }

    public Task<SdkCommandResult> InstallTemplatesAsync(string package, string version, bool trust, CancellationToken cancellationToken = default)
    {
        RequireTrust(trust);
        if (string.IsNullOrWhiteSpace(package) || package.Length > 200 || package.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || !char.IsAsciiLetterOrDigit(package[0]))
            throw new ArgumentException("Use a NuGet template package identifier, not a URL or filesystem path.");
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100 || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+')) || !char.IsAsciiDigit(version[0]))
            throw new ArgumentException("Choose an explicit template package version.");
        return RunLockedAsync(Files.Root, ["new", "install", package + "::" + version], cancellationToken);
    }

    public async Task<WorkspaceCreateResult> CreateAsync(WorkspaceCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); RequireTrust(request.Trust);
        WorkspacePath.ValidateName(request.Name);
        if (request.SolutionFormat is not ("sln" or "slnx")) throw new ArgumentException("Choose sln or slnx solution format.");
        var emptySolution = request.Template == "solution";
        if (emptySolution && !request.CreateSolution) throw new ArgumentException("The solution template requires a solution.");
        if (!emptySolution) ValidateTemplate(request.Template);
        var parent = request.ParentDirectory == null ? Files.Root : Files.Resolve(request.ParentDirectory);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The chosen parent directory must exist.");
        var destinationRelative = request.ParentDirectory == null ? request.Name : WorkspacePath.Normalize(request.ParentDirectory + "/" + request.Name);
        var destination = Files.Resolve(destinationRelative);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The destination already exists. Project creation never overwrites existing files.");
        var options = TemplateOptions(request);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Keep failed staging directories for diagnosis; do not recursively delete files emitted by trusted template code.
        var stagingName = ".xamlg-create-" + Guid.NewGuid().ToString("N");
        var stagingRelative = request.ParentDirectory == null ? stagingName : request.ParentDirectory + "/" + stagingName;
        var staging = Files.Resolve(stagingRelative);
        try
        {
            Directory.CreateDirectory(staging);
            var results = new List<SdkCommandResult>();
            if (request.CreateSolution)
            {
                var solution = await _runner.RunAsync(parent, ["new", "sln", "--name=" + request.Name,
                    "--output=" + staging, "--format=" + request.SolutionFormat, "--no-update-check"], cancellationToken).ConfigureAwait(false);
                results.Add(solution);
                if (solution.ExitCode != 0) return new(stagingRelative, Combine(results));
            }
            if (!emptySolution)
            {
                var projectDirectory = request.CreateSolution ? Path.Combine(staging, request.Name) : staging;
                var args = new List<string> { "new", request.Template, "--name=" + request.Name, "--output=" + projectDirectory, "--no-update-check" };
                args.AddRange(options);
                var project = await _runner.RunAsync(parent, args, cancellationToken).ConfigureAwait(false);
                results.Add(project);
                if (project.ExitCode != 0) return new(stagingRelative, Combine(results));
            }
            // Inventory through the confined adapter before publication. No generated symlink is followed.
            var inventory = Files.List(stagingRelative, cancellationToken);
            var generatedProjects = inventory.Where(entry => !entry.IsDirectory && WorkspacePath.IsProject(entry.Path)).ToArray();
            if (!emptySolution && generatedProjects.Length == 0) throw new InvalidOperationException("The template did not emit a supported project file. Staging was retained: " + stagingRelative);
            string entry;
            if (request.CreateSolution)
            {
                entry = Path.Combine(staging, request.Name + "." + request.SolutionFormat);
                if (!File.Exists(entry)) throw new InvalidOperationException("The SDK did not emit the requested solution format.");
                if (generatedProjects.Length != 0)
                {
                    var add = new List<string> { "sln", entry, "add" };
                    add.AddRange(generatedProjects.Select(project => Files.Resolve(project.Path)));
                    var result = await _runner.RunAsync(parent, add, cancellationToken).ConfigureAwait(false);
                    results.Add(result);
                    if (result.ExitCode != 0) return new(stagingRelative, Combine(results));
                }
            }
            else entry = Files.Resolve(generatedProjects[0].Path);
            var entryRelative = Path.GetRelativePath(staging, entry).Replace('\\', '/');
            cancellationToken.ThrowIfCancellationRequested();
            destination = Files.Resolve(destinationRelative);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Another operation created the destination. Staging was retained: " + stagingRelative);
            Directory.Move(Files.Resolve(stagingRelative), destination);
            return new(WorkspacePath.Normalize(destinationRelative + "/" + entryRelative), Combine(results));
        }
        finally { _operations.Release(); }
    }

    public async Task<SdkCommandResult> EditAsync(WorkspaceSdkEditRequest request, CancellationToken cancellationToken = default)
    {
        RequireTrust(request.Trust);
        var path = ExistingEntry(request.Path);
        var args = new List<string>();
        switch (request.Operation)
        {
            case "solution-add":
            case "solution-remove":
                if (WorkspacePath.IsProject(path)) throw new ArgumentException("Select a solution file.");
                args.AddRange(["sln", path, request.Operation == "solution-add" ? "add" : "remove", ExistingProject(request.Value)]);
                if (request.SolutionFolder != null && request.Operation == "solution-add")
                    args.AddRange(["--solution-folder", WorkspacePath.Normalize(request.SolutionFolder)]);
                break;
            case "reference-add":
            case "reference-remove":
                if (!WorkspacePath.IsProject(path)) throw new ArgumentException("Select a project file.");
                args.AddRange([request.Operation == "reference-add" ? "add" : "remove", path, "reference", ExistingProject(request.Value)]);
                break;
            case "package-add":
            case "package-remove":
                if (!WorkspacePath.IsProject(path)) throw new ArgumentException("Select a project file.");
                if (string.IsNullOrWhiteSpace(request.Value) || request.Value.Length > 200 || request.Value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || !char.IsAsciiLetterOrDigit(request.Value[0]))
                    throw new ArgumentException("Invalid package identifier.");
                args.AddRange([request.Operation == "package-add" ? "add" : "remove", path, "package", request.Value]);
                if (request.Operation == "package-add")
                {
                    if (string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 100 || request.Version.Any(char.IsControl)) throw new ArgumentException("Provide an explicit package version.");
                    args.Add("--version=" + request.Version); args.Add("--no-restore");
                }
                break;
            default: throw new ArgumentException("Unsupported SDK edit operation.");
        }
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Files.CheckHashAsync(request.Path, request.ExpectedHash ?? throw new ArgumentException("Read the project or solution before preparing an edit."), cancellationToken).ConfigureAwait(false);
            return await _runner.RunAsync(Files.Root, args, cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<SdkCommandResult> RunLockedAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _runner.RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private string ExistingEntry(string path)
    {
        var absolute = Files.Resolve(path);
        if (!WorkspacePath.IsEntry(absolute) || !File.Exists(absolute)) throw new ArgumentException("Choose an existing solution or project file.");
        return absolute;
    }
    private string ExistingProject(string path)
    {
        var absolute = ExistingEntry(path);
        if (!WorkspacePath.IsProject(absolute)) throw new ArgumentException("Choose a project file.");
        return absolute;
    }
    private static void RequireTrust(bool trust)
    {
        if (!trust) throw new InvalidOperationException("This SDK operation can execute project, package or template code on your computer. Explicit trust is required.");
    }
    private static void ValidateTemplate(string template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 128 || !char.IsAsciiLetter(template[0]) || template.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Choose an installed template short name.");
        if (template is "install" or "uninstall" or "update" or "list" or "search" or "details") throw new ArgumentException("Choose a project template, not a template management command.");
    }
    private static List<string> TemplateOptions(WorkspaceCreateRequest request)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Framework != null) { StudioWorkspaceEvaluator.ValidateBuildValue(request.Framework); options["framework"] = request.Framework; }
        if (request.Namespace != null)
        {
            if (request.Namespace.Length > 256 || request.Namespace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_'))) throw new ArgumentException("Invalid namespace.");
            options["namespace"] = request.Namespace;
        }
        if (request.TemplateOptions != null)
        {
            if (request.TemplateOptions.Count > 32) throw new ArgumentException("Template option limit exceeded.");
            foreach (var (key, value) in request.TemplateOptions)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || !char.IsAsciiLetter(key[0]) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                    value == null || value.Length > 1024 || value.Any(char.IsControl)) throw new ArgumentException("Invalid template option.");
                if (new[] { "output", "name", "force", "install", "uninstall", "update", "list", "search", "help", "dry-run", "debug", "allow-scripts", "add-source", "nuget-source" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Template engine options cannot override the workspace creation boundary.");
                if (!options.TryAdd(key, value)) throw new ArgumentException("Duplicate template option: " + key);
            }
        }
        return options.Select(pair => "--" + pair.Key + "=" + pair.Value).ToList();
    }
    private static SdkCommandResult Combine(IReadOnlyList<SdkCommandResult> results)
    {
        const int limit = 1024 * 1024;
        var stdout = string.Join('\n', results.Select(result => result.StandardOutput));
        var stderr = string.Join('\n', results.Select(result => result.StandardError));
        return new(results.LastOrDefault()?.ExitCode ?? 0, stdout[..Math.Min(stdout.Length, limit)], stderr[..Math.Min(stderr.Length, limit)],
            stdout.Length > limit || stderr.Length > limit || results.Any(result => result.OutputTruncated));
    }

    // The CLI's English fixed-column table is presentation, not a stable JSON protocol. Keep its raw
    // output authoritative, and return an empty parsed catalog instead of guessing on unfamiliar output.
    public static IReadOnlyList<NativeTemplate> ParseTemplates(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 1; index < lines.Length; index++)
        {
            var separators = Regex.Matches(lines[index], "-{2,}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (separators.Count < 2 || lines[index].Any(c => c != '-' && !char.IsWhiteSpace(c))) continue;
            var headers = separators.Select(match => Slice(lines[index - 1], match.Index, match.Length).Trim()).ToArray();
            var shortColumn = Array.FindIndex(headers, name => name.Equals("Short Name", StringComparison.OrdinalIgnoreCase));
            var nameColumn = Array.FindIndex(headers, name => name.Equals("Template Name", StringComparison.OrdinalIgnoreCase));
            if (shortColumn < 0 || nameColumn < 0) return [];
            var result = new List<NativeTemplate>();
            for (var row = index + 1; row < lines.Length && result.Count < 2000; row++)
            {
                if (string.IsNullOrWhiteSpace(lines[row])) break;
                string Column(string name)
                {
                    var column = Array.FindIndex(headers, header => header.Equals(name, StringComparison.OrdinalIgnoreCase));
                    return column < 0 ? "" : Slice(lines[row], separators[column].Index, separators[column].Length).Trim();
                }
                var shortNames = Column("Short Name");
                if (shortNames.Length == 0) continue;
                result.Add(new(Column("Template Name"), shortNames, Column("Language"), Column("Type"), Column("Tags"), Column("Author")));
            }
            return result;
        }
        return [];
    }
    private static string Slice(string value, int start, int length) => start >= value.Length ? "" : value.Substring(start, Math.Min(length, value.Length - start));
}
