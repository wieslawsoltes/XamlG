using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using XamlG.ProjectSystem;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class ProjectSystemTests
{
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\source\\app.csproj")]
    [InlineData("../outside")]
    [InlineData("src/../../outside")]
    [InlineData("\\\\server\\share")]
    [InlineData("App.cs:stream")]
    [InlineData("CON.txt")]
    [InlineData("src/Lpt1.cs")]
    [InlineData("src/COM¹.txt")]
    [InlineData("src/trailing.")]
    [InlineData("src/trailing ")]
    [InlineData("src/invalid?.cs")]
    public void Paths_reject_escapes_and_nonportable_names(string path) =>
        Assert.Throws<ArgumentException>(() => WorkspacePath.Normalize(path));

    [Fact]
    public void Paths_resolve_relative_references_without_losing_identity()
    {
        Assert.Equal("src/App/App.csproj", WorkspacePath.Normalize("src\\App/./App.csproj"));
        Assert.Equal("src/Library/Library.csproj", WorkspacePath.Resolve("src/App/App.csproj", "../Library/Library.csproj"));
        Assert.Equal("../Library/Library.csproj", WorkspacePath.RelativeTo("src/App/App.csproj", "src/Library/Library.csproj"));
        Assert.Throws<ArgumentException>(() => WorkspacePath.Resolve("src/App.csproj", "/outside.csproj"));
    }

    [Theory]
    [InlineData("A.cs", "a.cs")]
    [InlineData("src/A.cs", "Src/B.cs")]
    [InlineData("src", "src/A.cs")]
    public void Workspaces_reject_cross_platform_file_and_directory_collisions(string first, string second) =>
        Assert.Throws<ArgumentException>(() => Workspace((first, "a"), (second, "b")));

    [Fact]
    public void Transactions_check_revision_and_all_before_images_before_publication()
    {
        var workspace = Workspace(("A.cs", "old-a"), ("B.cs", "old-b"));
        var before = workspace.Current;
        Assert.Throws<InvalidOperationException>(() => workspace.Apply(before.Revision,
            [new("A.cs", new("old-a"), new("new-a")), new("B.cs", new("stale"), new("new-b"))]));
        Assert.Same(before, workspace.Current);
        var changed = workspace.Apply(before.Revision, [new("A.cs", new("old-a"), new("new-a"))]);
        Assert.Equal(before.Revision + 1, changed.Revision);
        Assert.Equal("old-b", changed.Files["B.cs"].Content);
        Assert.Throws<InvalidOperationException>(() => workspace.Apply(before.Revision, []));
        Assert.Same(changed, workspace.Apply(changed.Revision, [new("A.cs", new("new-a"), new("new-a"))]));
    }

    [Fact]
    public void Duplicate_transaction_paths_and_invalid_binary_content_do_not_mutate_workspace()
    {
        var workspace = Workspace(("A.cs", "a"));
        var before = workspace.Current;
        Assert.Throws<ArgumentException>(() => workspace.Apply(0,
            [new("A.cs", new("a"), new("b")), new("./A.cs", new("a"), new("c"))]));
        Assert.Throws<ArgumentException>(() => workspace.Apply(0, [new("image.bin", null, new("not base64!", true))]));
        Assert.Same(before, workspace.Current);
    }

    [Fact]
    public void File_move_is_atomic_and_rebinds_the_entry_without_rewriting_unknown_references()
    {
        var workspace = Workspace(("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("taken.csproj", "<Project />"));
        workspace.Open(0, "App.csproj");
        var before = workspace.Current;
        Assert.Throws<InvalidOperationException>(() => workspace.Move(before.Revision, "App.csproj", "taken.csproj"));
        Assert.Same(before, workspace.Current);
        var moved = workspace.Move(before.Revision, "App.csproj", "New.csproj");
        Assert.Equal("New.csproj", moved.EntryPath);
        Assert.False(moved.Files.ContainsKey("App.csproj"));
    }

    [Fact]
    public void Slnx_inspection_preserves_folders_items_frameworks_and_authored_conditions()
    {
        var workspace = Workspace(
            ("Demo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/App/App.csproj\" /><File Path=\"README.md\" /></Folder></Solution>"),
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks><UnknownProperty>keep me</UnknownProperty></PropertyGroup>
                  <PropertyGroup Condition="'$(Configuration)' == 'Release'"><DefineConstants>RELEASE_ONLY</DefineConstants></PropertyGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                    <ProjectReference Include="../Library/Library.csproj" />
                    <Compile Include="../../Shared.cs" Link="Shared/Shared.cs" />
                    <PackageReference Include="Example.Library" Version="1.2.3" />
                  </ItemGroup>
                  <Import Project="Custom.targets" />
                </Project>
                """),
            ("src/Library/Library.csproj", "<Project />"), ("README.md", "hello"));
        workspace.Open(0, "Demo.slnx");
        var solution = SolutionInspector.Inspect(workspace.Current);
        Assert.False(solution.Evaluated);
        Assert.Equal("src", Assert.Single(solution.Folders));
        Assert.Equal("README.md", Assert.Single(solution.SolutionItems));
        var project = Assert.Single(solution.Projects);
        Assert.True(project.Loaded);
        Assert.Equal("src", project.Folder);
        Assert.Equal(new[] { "net9.0", "net10.0" }, project.Definition!.TargetFrameworks);
        Assert.Equal("keep me", project.Definition.Properties["UnknownProperty"]);
        Assert.False(project.Definition.Properties.ContainsKey("DefineConstants"));
        Assert.Equal("src/Library/Library.csproj", Assert.Single(project.Definition.ProjectReferences));
        Assert.Contains("TargetFramework", project.Definition.Items[0].Condition!);
        Assert.Equal("1.2.3", project.Definition.Items.Single(item => item.Kind == "PackageReference").Version);
    }

    [Fact]
    public void Legacy_solutions_preserve_nested_folder_and_solution_item_identity()
    {
        var workspace = Workspace(("Demo.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{00000000-0000-0000-0000-000000000001}"
                ProjectSection(SolutionItems) = preProject
                    README.md = README.md
                EndProjectSection
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App.csproj", "{00000000-0000-0000-0000-000000000002}"
            EndProject
            Global
                GlobalSection(NestedProjects) = preSolution
                    {00000000-0000-0000-0000-000000000002} = {00000000-0000-0000-0000-000000000001}
                EndGlobalSection
            EndGlobal
            """), ("src/App.csproj", "<Project />"), ("README.md", "hello"));
        workspace.Open(0, "Demo.sln");
        var solution = SolutionInspector.Inspect(workspace.Current);
        Assert.Equal("src", Assert.Single(solution.Projects).Folder);
        Assert.Equal("src/App.csproj", Assert.Single(solution.Projects).Path);
        Assert.Equal("README.md", Assert.Single(solution.SolutionItems));
    }

    [Fact]
    public void Filters_resolve_project_paths_relative_to_the_underlying_solution()
    {
        var workspace = Workspace(("src/Demo.slnx", "<Solution><Project Path=\"A/A.csproj\" /><Project Path=\"B/B.csproj\" /></Solution>"),
            ("filters/OnlyA.slnf", "{\"solution\":{\"path\":\"../src/Demo.slnx\",\"projects\":[\"A/A.csproj\"]}}"),
            ("src/A/A.csproj", "<Project />"), ("src/B/B.csproj", "<Project />"));
        workspace.Open(0, "filters/OnlyA.slnf");
        var result = SolutionInspector.Inspect(workspace.Current);
        Assert.Equal("slnf", result.Format);
        Assert.True(result.Projects.Single(project => project.Name == "A").Loaded);
        Assert.False(result.Projects.Single(project => project.Name == "B").Loaded);
        Assert.Throws<ArgumentException>(() => workspace.SetStartupProject(workspace.Current.Revision, "src/B/B.csproj"));
    }

    [Fact]
    public void Xml_entities_and_recursive_filters_are_rejected()
    {
        Assert.Throws<XmlException>(() => SolutionInspector.ReadXml("<!DOCTYPE Project [<!ENTITY x SYSTEM 'file:///secret'>]><Project>&x;</Project>", "Project"));
        var workspace = Workspace(("recursive.slnf", "{\"solution\":{\"path\":\"recursive.slnf\",\"projects\":[]}}"));
        workspace.Open(0, "recursive.slnf");
        Assert.Throws<ArgumentException>(() => SolutionInspector.Inspect(workspace.Current));
    }

    [Fact]
    public void Missing_projects_and_external_references_are_diagnostics_not_fabricated_builds()
    {
        var workspace = Workspace(("Demo.slnx", "<Solution><Project Path=\"Missing.csproj\" /><Project Path=\"../Outside.csproj\" /></Solution>"));
        workspace.Open(0, "Demo.slnx");
        var result = SolutionInspector.Inspect(workspace.Current);
        Assert.False(Assert.Single(result.Projects).Loaded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("outside", StringComparison.OrdinalIgnoreCase) || diagnostic.Contains("cannot be resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void Xml_edits_preserve_unknown_targets_comments_conditions_and_namespaces()
    {
        var workspace = Workspace(("App.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <!-- keep this comment -->
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <Target Name="Custom"><Message Text="preserve" /></Target>
              <ItemGroup Condition="'$(Configuration)' == 'Release'"><Compile Remove="Debug.cs" /></ItemGroup>
            </Project>
            """));
        var edit = ProjectFileEditor.SetProperty(workspace.Current, "App.csproj", "TargetFramework", "net10.0");
        workspace.Apply(0, [edit]);
        var text = workspace.Current.Files["App.csproj"].Content;
        Assert.Contains("keep this comment", text);
        Assert.Contains("Target Name=\"Custom\"", text);
        Assert.Contains("Compile Remove=\"Debug.cs\"", text);
        var xml = XDocument.Parse(text);
        Assert.All(xml.Descendants(), node => Assert.Equal("http://schemas.microsoft.com/developer/msbuild/2003", node.Name.NamespaceName));
    }

    [Fact]
    public void Adding_a_project_and_its_solution_membership_is_one_atomic_transaction()
    {
        var workspace = WorkspaceTemplates.CreateSolution("Demo").CreateWorkspace();
        var plan = WorkspaceTemplates.CreateProject(new("classlib", "Library"));
        var membership = ProjectFileEditor.AddProject(workspace.Current, "Demo.slnx", plan.EntryPath, "src");
        var updated = workspace.Apply(0, plan.Changes.Append(membership));
        var project = Assert.Single(SolutionInspector.Inspect(updated).Projects);
        Assert.Equal("src", project.Folder);
        Assert.True(project.Loaded);
        Assert.Throws<InvalidOperationException>(() => workspace.Apply(updated.Revision, plan.Changes));
    }

    [Theory]
    [InlineData("console")]
    [InlineData("classlib")]
    [InlineData("avalonia.app")]
    [InlineData("avalonia.mvvm")]
    public void Offline_templates_have_valid_project_and_xaml_documents(string template)
    {
        var plan = WorkspaceTemplates.CreateSolution("Demo", new(template, "My.App"));
        var workspace = plan.CreateWorkspace();
        Assert.True(Assert.Single(SolutionInspector.Inspect(workspace.Current).Projects).Loaded);
        foreach (var (path, file) in plan.Files)
            if (path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".slnx", StringComparison.Ordinal))
                Assert.NotNull(XDocument.Parse(file.Content).Root);
        if (template.StartsWith("avalonia", StringComparison.Ordinal))
        {
            Assert.Contains("Avalonia.Desktop", plan.Files["My.App/My.App.csproj"].Content);
            Assert.Contains("AvaloniaXamlLoader.Load(this)", plan.Files["My.App/MainWindow.axaml.cs"].Content);
        }
    }

    [Fact]
    public void Template_namespace_is_a_valid_identifier_and_package_versions_are_explicit()
    {
        var plan = WorkspaceTemplates.CreateProject(new("avalonia.mvvm", "123-hello"));
        Assert.Contains("namespace _123_hello;", plan.Files["123-hello/Program.cs"].Content);
        Assert.Contains("Version=\"12.1.3\"", plan.Files[plan.EntryPath].Content);
        Assert.Throws<ArgumentException>(() => WorkspaceTemplates.CreateProject(new("avalonia.app", "App", AvaloniaVersion: "*")));
        Assert.Throws<ArgumentException>(() => WorkspaceTemplates.CreateProject(new("console", "App", Namespace: "class")));
    }

    private static VirtualWorkspace Workspace(params (string Path, string Content)[] files) =>
        new(files.Select(file => new KeyValuePair<string, WorkspaceFile>(file.Path, new(file.Content))));
}
