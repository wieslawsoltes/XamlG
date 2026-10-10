using System.Text;
using XamlG.ProjectSystem;
using XamlG.Workspaces.Studio;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class StudioWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XamlG-workspace-tests-" + Guid.NewGuid().ToString("N"));
    public StudioWorkspaceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Native_file_saves_preserve_encoding_and_reject_external_changes()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("original")).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "Test.txt"), bytes);
        var files = new WorkspaceFileSystem(_root);
        var original = await files.ReadAsync("Test.txt");
        Assert.Equal("utf-16le", original.Encoding);
        Assert.Equal("original", original.File.Content);
        var saved = await files.WriteAsync("Test.txt", original.Hash, new("updated"));
        Assert.Equal("utf-16le", saved.Encoding);
        Assert.NotEqual(original.Hash, saved.Hash);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, (await File.ReadAllBytesAsync(Path.Combine(_root, "Test.txt"))).Take(2));
        await File.WriteAllTextAsync(Path.Combine(_root, "Test.txt"), "external");
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.WriteAsync("Test.txt", saved.Hash, new("do not overwrite")));
        Assert.Equal("external", await File.ReadAllTextAsync(Path.Combine(_root, "Test.txt")));
        Assert.Empty(Directory.EnumerateFiles(_root, ".xamlg-write-*.tmp"));
    }

    [Fact]
    public async Task Native_file_create_move_and_delete_require_matching_before_images()
    {
        var files = new WorkspaceFileSystem(_root);
        var created = await files.WriteAsync("src/A.cs", null, new("class A {}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.WriteAsync("src/A.cs", null, new("overwrite")));
        var moved = await files.MoveAsync("src/A.cs", "src/B.cs", created.Hash);
        Assert.False(File.Exists(Path.Combine(_root, "src", "A.cs")));
        Assert.Equal(created.File, moved.File);
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.DeleteAsync("src/B.cs", "stale"));
        await files.DeleteAsync("src/B.cs", moved.Hash);
        Assert.False(File.Exists(Path.Combine(_root, "src", "B.cs")));
    }

    [Fact]
    public async Task Binary_assets_round_trip_without_text_transcoding()
    {
        var files = new WorkspaceFileSystem(_root);
        byte[] bytes = [0, 0xFF, 0xC0, 1, 2, 3];
        var created = await files.WriteAsync("Assets/icon.bin", null, new(Convert.ToBase64String(bytes), true));
        Assert.True(created.File.IsBinary);
        Assert.Equal(bytes, Convert.FromBase64String(created.File.Content));
    }

    [Fact]
    public void Root_policy_rejects_escapes_and_version_control_internals()
    {
        var files = new WorkspaceFileSystem(_root);
        Assert.Throws<ArgumentException>(() => files.Resolve("../outside"));
        Assert.Throws<UnauthorizedAccessException>(() => files.Resolve(".git/config"));
        Assert.Throws<UnauthorizedAccessException>(() => files.Resolve("src/.GIT/config"));
    }

    [Fact]
    public async Task Root_policy_does_not_follow_descendant_symlinks()
    {
        // Windows symlink creation requires a machine-level privilege not available on every test host.
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(Path.GetTempPath(), "XamlG-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            var files = new WorkspaceFileSystem(_root);
            Assert.Throws<UnauthorizedAccessException>(() => files.Resolve("linked/secret.txt"));
            Assert.DoesNotContain(files.List(), entry => entry.Path.StartsWith("linked", StringComparison.Ordinal));
        }
        finally { Directory.Delete(Path.Combine(_root, "linked")); Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task Native_sdk_entry_points_reject_untrusted_requests_before_execution()
    {
        var runner = new RecordingRunner();
        var service = new StudioWorkspaceService(_root, runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BuildAsync(new("App.csproj", "build")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(new("App.csproj")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new("App")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TemplatesAsync(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallTemplatesAsync("Avalonia.Templates", "12.1.3", false));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Build_uses_argument_boundaries_and_validated_configuration_properties()
    {
        var runner = new RecordingRunner();
        var service = new StudioWorkspaceService(_root, runner);
        Directory.CreateDirectory(Path.Combine(_root, "with spaces"));
        await File.WriteAllTextAsync(Path.Combine(_root, "with spaces", "App.csproj"), "<Project />");
        var result = await service.BuildAsync(new("with spaces/App.csproj", "rebuild", true, "Release", "Any CPU", "net10.0"));
        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("build", call.Arguments[0]);
        Assert.Equal(Path.Combine(service.Files.Root, "with spaces", "App.csproj"), call.Arguments[1]);
        Assert.Contains("--no-incremental", call.Arguments);
        Assert.Contains("-p:Platform=Any CPU", call.Arguments);
        await Assert.ThrowsAsync<ArgumentException>(() => service.BuildAsync(new("with spaces/App.csproj", "build", true, "Release;Target=Other")));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Template_creation_is_staged_and_never_overwrites_an_existing_destination()
    {
        var runner = new RecordingRunner
        {
            Handler = arguments =>
            {
                if (arguments[0] != "new") return;
                var output = arguments.Single(argument => argument.StartsWith("--output=", StringComparison.Ordinal))[9..];
                var name = arguments.Single(argument => argument.StartsWith("--name=", StringComparison.Ordinal))[7..];
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, name + (arguments[1] == "sln" ? ".slnx" : ".csproj")), arguments[1] == "sln" ? "<Solution />" : "<Project />");
            }
        };
        var service = new StudioWorkspaceService(_root, runner);
        var created = await service.CreateAsync(new("Demo", Framework: "net10.0", Trust: true));
        Assert.Equal("Demo/Demo.slnx", created.EntryPath);
        Assert.True(File.Exists(Path.Combine(_root, "Demo", "Demo.slnx")));
        Assert.True(File.Exists(Path.Combine(_root, "Demo", "Demo", "Demo.csproj")));
        Assert.Equal(3, runner.Calls.Count);
        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(new("Demo", Trust: true)));
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Template_engine_options_cannot_redirect_or_force_workspace_creation()
    {
        var runner = new RecordingRunner(); var service = new StudioWorkspaceService(_root, runner);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new("Demo", Trust: true,
            TemplateOptions: new Dictionary<string, string> { ["output"] = "../outside" })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new("Demo", Template: "install", Trust: true)));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Cancelled_sdk_requests_do_not_enter_the_runner()
    {
        var runner = new RecordingRunner(); var service = new StudioWorkspaceService(_root, runner);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TemplatesAsync(true, cancelled.Token));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Unknown_template_output_is_not_parsed_as_an_authoritative_catalog()
    {
        Assert.Empty(StudioWorkspaceService.ParseTemplates("unrecognized output"));
        var table = "Template Name  Short Name  Language  Type     Tags\n-------------  ----------  --------  -------  ----\nConsole App    console     [C#],F#   project  App\n";
        var template = Assert.Single(StudioWorkspaceService.ParseTemplates(table));
        Assert.Equal("console", template.ShortNames);
        Assert.Equal("Console App", template.Name);
    }

    private sealed class RecordingRunner : ISdkProcessRunner
    {
        public List<(string Directory, string[] Arguments)> Calls { get; } = [];
        public Action<IReadOnlyList<string>>? Handler { get; init; }
        public Task<SdkCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((workingDirectory, arguments.ToArray()));
            Handler?.Invoke(arguments);
            return Task.FromResult(new SdkCommandResult(0, "ok", "", false));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}
