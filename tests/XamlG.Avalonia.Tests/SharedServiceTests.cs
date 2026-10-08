using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SharedServiceTests
{
    private const string Ns = ResourceProjectFixture.Namespace +
        " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaFact]
    public void SharedImplementationKeepsEachDocumentsRootTargetAndNamespaceScope()
    {
        var fixture = new ResourceProjectFixture(new[]
        {
            ("First.axaml", "<Border " + Ns + " xmlns:a='clr-namespace:First' Tag='{t:SharedServiceCapture}'/>"),
            ("Second.axaml", "<Border " + Ns + " xmlns:a='clr-namespace:Second' Tag='{t:SharedServiceCapture}'/>")
        });
        var assembly = ResourceProjectFixture.Load(fixture.Emit());
        Border Build(string path)
        {
            var output = fixture.Result.Documents.Single(d => d.Input.LogicalPath == path).Output;
            return Assert.IsType<Border>(assembly.GetType(output.FactoryMetadataName)!
                .GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { null }));
        }
        var first = Build("First.axaml");
        var second = Build("Second.axaml");
        foreach (var (root, ns) in new[] { (first, "First"), (second, "Second") })
        {
            var captured = Assert.IsType<SharedServiceCapture>(root.Tag);
            Assert.Same(root, captured.Root.RootObject);
            Assert.Same(root, captured.Target.TargetObject);
            Assert.Equal(ns, Assert.Single(captured.Namespaces.XmlNamespaces["a"]).ClrNamespace);
        }
    }

    [AvaloniaFact]
    public void AFailedFormerOwnerDoesNotRemoveServicesFromCachedValidOutputs()
    {
        var fixture = new ResourceProjectFixture(Array.Empty<(string, string)>());
        var compiler = new XamlProjectCompiler();
        var profile = AvaloniaFrameworkProfile.Create();
        static XamlProjectDocument Document(string path, string attributes) =>
            new(XamlSyntaxTree.Parse("<Border " + Ns + " " + attributes + "/>", path), path);
        var first = Document("First.axaml", "Width='1'");
        var second = Document("Second.axaml", "Width='2'");
        var initial = compiler.Compile(new[] { first, second }, fixture.Compilation, profile);
        Assert.True(initial.Success);
        var failed = compiler.Compile(new[] { Document("First.axaml", "Width='invalid'"), second }, fixture.Compilation, profile);
        Assert.False(failed.Success);
        Assert.Equal(1, failed.Statistics.ReusedOutputs);
        var survivor = failed.Documents.Single(d => d.Input.LogicalPath == "Second.axaml").Output;
        using var bytes = new MemoryStream();
        var emitted = fixture.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(survivor.Source,
            new CSharpParseOptions(LanguageVersion.Preview))).Emit(bytes);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var recovered = compiler.Compile(new[] { first, second }, fixture.Compilation, profile);
        Assert.True(recovered.Success);
        Assert.Same(initial.Documents[1].Output, recovered.Documents[1].Output);
    }
}

public sealed record SharedServiceCapture(IRootObjectProvider Root, IProvideValueTarget Target,
    IAvaloniaXamlIlXmlNamespaceInfoProvider Namespaces);

public sealed class SharedServiceCaptureExtension
{
    public SharedServiceCapture ProvideValue(IServiceProvider services) => new(
        (IRootObjectProvider)services.GetService(typeof(IRootObjectProvider))!,
        (IProvideValueTarget)services.GetService(typeof(IProvideValueTarget))!,
        (IAvaloniaXamlIlXmlNamespaceInfoProvider)services.GetService(typeof(IAvaloniaXamlIlXmlNamespaceInfoProvider))!);
}
