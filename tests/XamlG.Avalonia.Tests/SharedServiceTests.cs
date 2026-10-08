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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedImplementationKeepsEachDocumentsRootTargetAndNamespaceScope(bool differentNamespaces)
    {
        var secondNamespace = differentNamespaces ? "Second" : "First";
        var fixture = new ResourceProjectFixture(new[]
        {
            ("First.axaml", "<Border " + Ns + " xmlns:a='clr-namespace:First' Tag='{t:SharedServiceCapture}'/>"),
            ("Second.axaml", "<Border " + Ns + " xmlns:a='clr-namespace:" + secondNamespace + "' Tag='{t:SharedServiceCapture}'/>")
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
        foreach (var (root, ns, path) in new[] { (first, "First", "First.axaml"), (second, secondNamespace, "Second.axaml") })
        {
            var captured = Assert.IsType<SharedServiceCapture>(root.Tag);
            Assert.Same(root, captured.Root.RootObject);
            Assert.Same(root, captured.Target.TargetObject);
            Assert.Equal(ns, Assert.Single(captured.Namespaces.XmlNamespaces["a"]).ClrNamespace);
            Assert.Equal(fixture.Result.Documents.Single(document => document.Input.LogicalPath == path).Document.Options.BaseUri,
                captured.Uri.BaseUri!.OriginalString);
            Assert.Same(captured.Names, NameScope.GetNameScope(root));
        }
        var firstCapture = Assert.IsType<SharedServiceCapture>(first.Tag);
        var secondCapture = Assert.IsType<SharedServiceCapture>(second.Tag);
        Assert.NotSame(firstCapture.Names, secondCapture.Names);
        if (!differentNamespaces) Assert.Equal(firstCapture.Root.GetType(), secondCapture.Root.GetType());
        firstCapture.Uri.BaseUri = new Uri("xamlg://changed/first");
        Assert.NotEqual(firstCapture.Uri.BaseUri, secondCapture.Uri.BaseUri);
        var rebuilt = Build("First.axaml");
        var rebuiltCapture = Assert.IsType<SharedServiceCapture>(rebuilt.Tag);
        Assert.NotEqual(firstCapture.Uri.BaseUri, rebuiltCapture.Uri.BaseUri);
        Assert.NotSame(firstCapture.Names, rebuiltCapture.Names);
        var firstMap = Assert.IsType<SharedServiceCapture>(first.Tag).Namespaces.XmlNamespaces;
        var secondMap = Assert.IsType<SharedServiceCapture>(second.Tag).Namespaces.XmlNamespaces;
        Assert.NotSame(firstMap, secondMap);
        Assert.NotSame(firstMap["a"], secondMap["a"]);
        Assert.NotSame(firstMap["a"][0], secondMap["a"][0]);
        firstMap["a"][0].ClrNamespace = "Changed";
        Assert.Equal(secondNamespace, secondMap["a"][0].ClrNamespace);
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
    IAvaloniaXamlIlXmlNamespaceInfoProvider Namespaces, IUriContext Uri, INameScope Names);

public sealed class SharedServiceCaptureExtension
{
    public SharedServiceCapture ProvideValue(IServiceProvider services) => new(
        (IRootObjectProvider)services.GetService(typeof(IRootObjectProvider))!,
        (IProvideValueTarget)services.GetService(typeof(IProvideValueTarget))!,
        (IAvaloniaXamlIlXmlNamespaceInfoProvider)services.GetService(typeof(IAvaloniaXamlIlXmlNamespaceInfoProvider))!,
        (IUriContext)services.GetService(typeof(IUriContext))!,
        (INameScope)services.GetService(typeof(INameScope))!);
}
