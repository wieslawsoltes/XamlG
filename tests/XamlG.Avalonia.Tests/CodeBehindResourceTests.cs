using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class CodeBehindResourceTests
{
    private const string Code = """
        using Avalonia.Controls;
        namespace Resources {
          public partial class Palette : ResourceDictionary {
            public static int Constructions;
            public Palette() { InitializeComponent(); Constructions++; }
          }
        }
        """;
    private static string Palette => "<ResourceDictionary " + ResourceProjectFixture.Namespace + " x:Class='Resources.Palette'><x:String x:Key='shared'>class resource</x:String></ResourceDictionary>";
    [AvaloniaFact]
    public void LocalCodeBehindDictionaryIsInitializedOnceAndOwnedByItsCaller()
    {
        var project = new ResourceProjectFixture(new[]
        {
            ("Palette.axaml", Palette),
            ("Main.axaml", ResourceProjectFixture.Dictionary(ResourceProjectFixture.Include("Palette.axaml")))
        }, sourceCode: Code);
        var root = (ResourceDictionary)project.Build("Main.axaml");
        var included = Assert.Single(root.MergedDictionaries);
        Assert.Equal("class resource", ((ResourceDictionary)included)["shared"]);
        Assert.Equal(1, included.GetType().GetField("Constructions")!.GetValue(null));
        Assert.True(XamlRuntimeSession.TryGet(included, out var child));
        Assert.True(XamlRuntimeSession.TryGet(root, out var owner)); owner!.Dispose();
        Assert.True(child!.IsDisposed);
    }
    [AvaloniaFact]
    public void PublicCodeBehindDictionaryExportsAReferencedFactory()
    {
        var name = "ClassResources_" + Guid.NewGuid().ToString("N");
        var producer = new ResourceProjectFixture(new[] { ("Palette.axaml", Palette) }, name, sourceCode: Code);
        var image = producer.Emit(); ResourceProjectFixture.Load(image);
        var consumer = new ResourceProjectFixture(new[]
        {
            ("Main.axaml", ResourceProjectFixture.Dictionary(ResourceProjectFixture.Include("avares://" + name + "/Palette.axaml")))
        }, references: new[] { MetadataReference.CreateFromImage(image) });
        var root = (ResourceDictionary)consumer.Build("Main.axaml");
        Assert.True(root.TryGetResource("shared", null, out var value)); Assert.Equal("class resource", value);
        Assert.Contains(consumer.Result.Resources.Resources, resource => resource.ExternalFactory?.Name.StartsWith("__XamlGBuild_", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("AvaloniaXamlLoader", consumer.Result.Documents[0].Output.Source);
    }
}
