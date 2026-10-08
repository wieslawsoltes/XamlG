using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ContentMetadataTests
{
    private const string Model = """
        using XamlG.Runtime;
        namespace Demo;
        public class Base { [Content] public string Text { get; set; } }
        [Content("Missing")] public class Broken : Base { }
        [Content("Value")] public class Override : Broken { public string Value { get; set; } }
        """;

    [Fact]
    public void MissingNamedContentIsDiagnosedAtTheObject()
    {
        var result = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Broken xmlns='clr-namespace:Demo'>text</Broken>"), CompilationFactory.Create(Model));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "XG1026" && diagnostic.Message.Contains("Missing") && diagnostic.Span.Start == 1);
    }

    [Fact]
    public void AValidOverrideStopsInheritedContentMetadataLookup()
    {
        using var code = CompiledXaml.Create("<Override xmlns='clr-namespace:Demo'>text</Override>", Model);
        var root = code.Build();
        Assert.Equal("text", root.GetType().GetProperty("Value")!.GetValue(root));
        Assert.Null(root.GetType().GetProperty("Text")!.GetValue(root));
    }
}
