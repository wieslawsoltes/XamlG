using System.Text;
using System.Xml.Linq;
using XamlG.ProjectSystem;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class ProjectFileEditingTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("utf-32")]
    public void Structural_edits_preserve_the_declared_encoding_and_export_valid_bytes(string encoding)
    {
        var workspace = Workspace($"<?xml version=\"1.0\" encoding=\"{encoding}\" standalone=\"yes\"?>\n<Project><PropertyGroup><RootNamespace>Before</RootNamespace></PropertyGroup></Project>");
        var edit = ProjectFileEditor.SetProperty(workspace.Current, "App/App.csproj", "RootNamespace", "Après");
        var file = edit.Replacement!;
        var xml = XDocument.Parse(file.Content);
        Assert.Equal(encoding, xml.Declaration!.Encoding);
        Assert.Equal("yes", xml.Declaration.Standalone);
        using var stream = new MemoryStream(WorkspaceArchive.Encode(file));
        Assert.Equal("Après", XDocument.Load(stream).Root!.Element("PropertyGroup")!.Element("RootNamespace")!.Value);
    }

    [Fact]
    public void Declaration_without_encoding_and_declaration_free_xml_remain_that_way()
    {
        foreach (var declaration in new[] { "", "<?xml version=\"1.0\"?>\n" })
        {
            var workspace = Workspace(declaration + "<Project />");
            var xml = XDocument.Parse(ProjectFileEditor.SetProperty(workspace.Current, "App/App.csproj", "RootNamespace", "Sample").Replacement!.Content);
            Assert.True(string.IsNullOrEmpty(xml.Declaration?.Encoding));
            Assert.Equal(declaration.Length != 0, xml.Declaration != null);
        }
    }

    [Theory]
    [InlineData("../Library/Library.csproj")]
    [InlineData("..\\Library\\Library.csproj")]
    [InlineData("../Library/./Library.csproj")]
    public void Project_reference_addition_matches_normalized_identity(string authored)
    {
        var workspace = Workspace($"<Project><ItemGroup><ProjectReference Include=\"{authored}\"><Private>False</Private></ProjectReference></ItemGroup></Project>");
        var edit = ProjectFileEditor.AddProjectReference(workspace.Current, "App/App.csproj", "Library/Library.csproj");
        var reference = Assert.Single(XDocument.Parse(edit.Replacement!.Content).Descendants("ProjectReference"));
        Assert.Equal(authored, reference.Attribute("Include")!.Value);
        Assert.Equal("False", reference.Element("Private")!.Value);
    }

    [Theory]
    [InlineData("<Version>1.0.0</Version>")]
    [InlineData("<Version>0.1.0</Version><Version>1.0.0</Version>")]
    public void Updating_package_version_updates_the_effective_unconditional_metadata_element(string metadata)
    {
        var workspace = Workspace($"<Project><ItemGroup><PackageReference Include=\"Example.Library\">{metadata}<PrivateAssets>all</PrivateAssets></PackageReference></ItemGroup></Project>");
        var edit = ProjectFileEditor.AddPackageReference(workspace.Current, "App/App.csproj", "example.library", "2.0.0");
        var reference = Assert.Single(XDocument.Parse(edit.Replacement!.Content).Descendants("PackageReference"));
        Assert.Null(reference.Attribute("Version"));
        Assert.Equal("2.0.0", reference.Elements("Version").Last().Value);
        Assert.Equal("all", reference.Element("PrivateAssets")!.Value);
    }

    [Fact]
    public void Conditional_versions_and_central_version_management_are_not_rewritten()
    {
        var workspace = Workspace("""
            <Project><ItemGroup>
              <PackageReference Include="Example.Library"><Version>1.0.0</Version><Version Condition="'$(Configuration)' == 'Preview'">3.0.0-preview</Version></PackageReference>
              <PackageReference Include="Centrally.Managed" />
            </ItemGroup></Project>
            """);
        var edit = ProjectFileEditor.AddPackageReference(workspace.Current, "App/App.csproj", "Example.Library", "2.0.0");
        workspace.Apply(workspace.Current.Revision, [edit]);
        edit = ProjectFileEditor.AddPackageReference(workspace.Current, "App/App.csproj", "Centrally.Managed", null);
        var references = XDocument.Parse(edit.Replacement!.Content).Descendants("PackageReference").ToArray();
        Assert.Equal("2.0.0", references[0].Elements("Version").First().Value);
        Assert.Equal("3.0.0-preview", references[0].Elements("Version").Last().Value);
        Assert.Null(references[1].Attribute("Version"));
        Assert.Empty(references[1].Elements());
    }

    [Fact]
    public void Removing_references_preserves_conditions_imports_updates_and_unrelated_metadata()
    {
        var workspace = Workspace("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <!-- keep -->
              <Import Project="Dependencies.props" />
              <ItemGroup>
                <ProjectReference Include="..\Library\Library.csproj" />
                <ProjectReference Include="$(SharedProject)" />
                <PackageReference Include="Example.Library" Version="1.0.0" />
                <PackageReference Update="Example.Library" PrivateAssets="all" />
                <PackageReference Include="Example.Library" Condition="'$(Configuration)' == 'Debug'" Version="2.0.0" />
              </ItemGroup>
              <ItemGroup Condition="'$(Configuration)' == 'Release'"><ProjectReference Include="../Library/Library.csproj" /></ItemGroup>
            </Project>
            """);
        workspace.Apply(0, [ProjectFileEditor.RemoveProjectReference(workspace.Current, "App/App.csproj", "Library/Library.csproj")]);
        workspace.Apply(workspace.Current.Revision, [ProjectFileEditor.RemovePackageReference(workspace.Current, "App/App.csproj", "example.library")]);
        var text = workspace.Current.Files["App/App.csproj"].Content;
        var xml = XDocument.Parse(text); XNamespace ns = xml.Root!.Name.Namespace;
        Assert.Equal(2, xml.Descendants(ns + "ProjectReference").Count());
        Assert.Equal(2, xml.Descendants(ns + "PackageReference").Count());
        Assert.Single(xml.Descendants(ns + "Import"));
        Assert.Contains("<!-- keep -->", text);
        Assert.DoesNotContain(xml.Descendants(ns + "PackageReference"), item => item.Attribute("Include") != null && item.Attribute("Condition") == null);
    }

    private static VirtualWorkspace Workspace(string project) => new(new Dictionary<string, WorkspaceFile>
    {
        ["App/App.csproj"] = new(project), ["Library/Library.csproj"] = new("<Project />")
    });
}
