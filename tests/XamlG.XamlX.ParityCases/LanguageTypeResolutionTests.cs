using System;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class LanguageTypeResolutionTests : CompilerTestBase
{
    private const string Ns = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("Version", typeof(Version))]
    [InlineData("IDisposable", typeof(IDisposable))]
    [InlineData("TypeCode", typeof(TypeCode))]
    [InlineData("Environment", typeof(Environment))]
    public void LanguageNamespaceResolvesPublicSystemTypes(string name, Type expected)
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + " Value='{x:Type x:" + name + "}'/>");
        Assert.Equal(expected, root.Value);
    }

    [Theory]
    [InlineData("<x:Version>1.2.3</x:Version>")]
    [InlineData("<x:Version><x:Arguments><x:Int32>1</x:Int32><x:Int32>2</x:Int32><x:Int32>3</x:Int32></x:Arguments></x:Version>")]
    public void LanguageTypesSupportNormalConversionAndConstruction(string value)
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + "><ProviderResolutionRoot.Value>" + value + "</ProviderResolutionRoot.Value></ProviderResolutionRoot>");
        Assert.Equal(new Version(1, 2, 3), root.Value);
    }

    [Fact]
    public void LanguageEnumAndStaticMembersUseNormalResolution()
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + " Value='{x:Static x:TypeCode.Int32}'/>");
        Assert.Equal(TypeCode.Int32, root.Value);
    }

    [Theory]
    [InlineData("sys:Uri", typeof(Uri))]
    [InlineData("ResolutionContainer+Nested", typeof(ResolutionContainer.Nested))]
    [InlineData("ResolutionContainer+Generic(x:Int32)", typeof(ResolutionContainer.Generic<int>))]
    public void FacadesAndNestedMetadataTypesRetainTheirResolvedIdentity(string name, Type expected)
    {
        var root = (ProviderResolutionRoot)CompileAndRun("<ProviderResolutionRoot" + Ns + " xmlns:sys='clr-namespace:System;assembly=netstandard' Value='{x:Type " + name + "}'/>");
        Assert.Equal(expected, root.Value);
    }
}

public static class ResolutionContainer
{
    public sealed class Nested { }
    public sealed class Generic<T> { }
}
