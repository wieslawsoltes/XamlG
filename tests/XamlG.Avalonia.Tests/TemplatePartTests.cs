using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class TemplatePartTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("PartControl", "<Border/>", "XG3113", "AVLN2205", XamlSeverity.Error)]
    [InlineData("PartControl", "<TextBlock Name='PART_Body'/>", "XG3115", "AVLN2207", XamlSeverity.Error)]
    [InlineData("PartControl", "<TextBlock x:Name='PART_Body'/>", "XG3115", "AVLN2207", XamlSeverity.Error)]
    [InlineData("InheritedPartControl", "<Border/>", "XG3113", "AVLN2205", XamlSeverity.Error)]
    [InlineData("OptionalPartControl", "<Border/>", "XG3114", "AVLN2206", XamlSeverity.Info)]
    [InlineData("PartControl", "<ItemsControl><ItemsControl.ItemTemplate><DataTemplate><Border Name='PART_Body'/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>", "XG3113", "AVLN2205", XamlSeverity.Error)]
    public void PartDiagnosticsMatchTheUpstreamCompiler(string type, string body, string code, string upstreamCode, XamlSeverity severity)
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='t:" + type + "'>" + body + "</ControlTemplate>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        var expected = Assert.Single(baseline.Diagnostics, d => d.Id == upstreamCode);
        Assert.Equal(severity == XamlSeverity.Error ? RuntimeXamlDiagnosticSeverity.Error : RuntimeXamlDiagnosticSeverity.Info, expected.Severity);
        Assert.Equal(severity == XamlSeverity.Error, baseline.Error != null);
        var project = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) });
        var diagnostic = Assert.Single(project.Result.Documents.Single().Output.Diagnostics, d => d.Code == code);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.Equal(severity != XamlSeverity.Error, project.Result.Success);
        if (code == "XG3115") Assert.Equal("PART_Body", xaml.Substring(diagnostic.Span.Start, diagnostic.Span.Length));
    }

    [AvaloniaTheory]
    [InlineData("PartControl", "<Border Name='PART_Body'/>")]
    [InlineData("InheritedPartControl", "<Border x:Name='PART_Body'/>")]
    [InlineData("OverriddenPartControl", "<TextBlock Name='PART_Body'/>")]
    public void ValidAndOverriddenPartsCompile(string type, string body)
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='t:" + type + "'>" + body + "</ControlTemplate>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        Assert.Empty(baseline.Diagnostics);
        var project = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) });
        Assert.Empty(project.Result.Documents.Single().Output.Diagnostics);
        Assert.NotNull(project.Build("Template.axaml"));
    }

    [AvaloniaTheory]
    [InlineData("<t:PartControl><t:PartControl.Template><ControlTemplate><Border/></ControlTemplate></t:PartControl.Template></t:PartControl>")]
    [InlineData("<ControlTheme TargetType='{x:Type t:PartControl}'><Setter Property='Template'><ControlTemplate><Border/></ControlTemplate></Setter></ControlTheme>")]
    [InlineData("<StackPanel><Border Name='PART_Body'/><t:PartControl><t:PartControl.Template><ControlTemplate><Border/></ControlTemplate></t:PartControl.Template></t:PartControl></StackPanel>")]
    public void InferredTemplateTargetsUseAnIndependentNamescope(string content)
    {
        var xaml = content.Insert(content.IndexOf('>'), " " + Ns);
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.NotNull(baseline.Error);
        Assert.Contains(baseline.Diagnostics, d => d.Id == "AVLN2205");
        var project = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) });
        Assert.False(project.Result.Success);
        Assert.Contains(project.Result.Documents.Single().Output.Diagnostics, d => d.Code == "XG3113");
    }
}

[TemplatePart("PART_Body", typeof(Border), IsRequired = true)]
public class PartControl : TemplatedControl { }
public sealed class InheritedPartControl : PartControl { }
[TemplatePart(Name = "PART_Body", Type = typeof(TextBlock), IsRequired = true)]
public sealed class OverriddenPartControl : PartControl { }
[TemplatePart("PART_Body", typeof(Border))]
public sealed class OptionalPartControl : TemplatedControl { }
