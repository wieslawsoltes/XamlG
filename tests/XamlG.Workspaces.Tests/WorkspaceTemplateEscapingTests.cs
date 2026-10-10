using System.Xml.Linq;
using XamlG.ProjectSystem;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class WorkspaceTemplateEscapingTests
{
    public static IEnumerable<object[]> Names()
    {
        foreach (var template in new[] { "avalonia.app", "avalonia.mvvm" })
            foreach (var name in new[] { "App", "R&D", "A & B", "{Binding}", "{}AlreadyLiteral", "O'Brien", "Amp&amp;Literal", "Demo😀" })
                yield return [template, name];
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Avalonia_starters_preserve_project_names_as_literal_xml_attribute_data(string template, string name)
    {
        var plan = WorkspaceTemplates.CreateSolution("Solution", new(template, name));
        foreach (var (path, file) in plan.Files)
            if (path.EndsWith(".axaml", StringComparison.Ordinal) || path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".slnx", StringComparison.Ordinal))
                Assert.NotNull(XDocument.Parse(file.Content).Root);

        var window = XDocument.Parse(plan.Files[name + "/MainWindow.axaml"].Content).Root!;
        // The XAML literal escape is consumed by the loader, not the XML parser.
        // Preserve entity-like strings and existing escape prefixes exactly once.
        Assert.Equal("{}" + name, window.Attribute("Title")!.Value);
        Assert.Single(window.Attributes("Title"));
        var project = Assert.Single(SolutionInspector.Inspect(plan.CreateWorkspace().Current).Projects);
        Assert.Equal(name + "/" + name + ".csproj", project.Path);
        Assert.True(project.Loaded);
    }
}
