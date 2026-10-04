using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ResourceCacheRecoveryTests
{
    [Fact]
    public void BrokenDependenciesDoNotPoisonCachedCallerBindings()
    {
        static XamlProjectDocument Document(string path, string body) => new(XamlSyntaxTree.Parse(ResourceProjectFixture.Dictionary(body), path), path);
        var compilation = new ResourceProjectFixture(Array.Empty<(string, string)>()).Compilation;
        var compiler = new XamlProjectCompiler();
        var profile = AvaloniaFrameworkProfile.Create();
        var cancellation = TestContext.Current.CancellationToken;
        var caller = Document("Main.axaml", ResourceProjectFixture.Include("Values.axaml"));
        var resource = Document("Values.axaml", "<x:Int32 x:Key='number'>7</x:Int32>");
        var initial = compiler.Compile(new[] { caller, resource }, compilation, profile, cancellationToken: cancellation);
        Assert.True(initial.Success);
        var broken = Document("Values.axaml", "<x:Int32 x:Key='number'>not an integer</x:Int32>");
        var rejected = compiler.Compile(new[] { caller, broken }, compilation, profile, cancellationToken: cancellation);
        Assert.False(rejected.Success);
        Assert.Equal(1, rejected.Statistics.BoundDocuments);
        Assert.Contains(rejected.Documents[0].Output.Diagnostics, d => d.Code == "XG3305");
        var recovered = compiler.Compile(new[] { caller, resource }, compilation, profile, cancellationToken: cancellation);
        Assert.True(recovered.Success);
        Assert.Equal(1, recovered.Statistics.ReusedBindings);
        Assert.Same(initial.Documents[0].Output, recovered.Documents[0].Output);
    }
}
