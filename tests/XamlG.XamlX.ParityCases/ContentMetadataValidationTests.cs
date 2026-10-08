using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class ContentMetadataValidationTests : CompilerTestBase
{
    [Theory]
    [InlineData("InvalidContentWithBase")]
    [InlineData("InheritedInvalidContent")]
    [InlineData("InvalidContentWithAdder")]
    [InlineData("InheritedAmbiguousContent")]
    public void InvalidContentDeclarationsDoNotFallBackToOtherContent(string type)
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<" + type + " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests'>text</" + type + ">"));
    }
}

[Content] public class InvalidContentWithBase : ContentBase { }
public sealed class InheritedInvalidContent : InvalidContentWithBase { }
[Content] public sealed class InvalidContentWithAdder { public void Add(string text) { } }
public class AmbiguousContentBase
{
    [Content] public string? First { get; set; }
    [Content] public string? Second { get; set; }
}
public sealed class InheritedAmbiguousContent : AmbiguousContentBase { }
