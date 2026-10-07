using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using XamlX;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class InheritedMetadataTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests'";

    [Theory]
    [InlineData("InheritedInitialization", false)]
    [InlineData("OverriddenInitialization", true)]
    public void InitializationMetadataUsesTheClosestDeclaration(string type, bool initializedWhenAdded)
    {
        var root = (InitializationRecorder)CompileAndRun("<InitializationRecorder" + Namespace + "><" + type + "/></InitializationRecorder>");
        Assert.Equal(initializedWhenAdded, root.InitializedWhenAdded);
        Assert.True(root.Item!.Initialized);
    }

    [Theory]
    [InlineData("PlainMetadataItem", "first ", " last")]
    [InlineData("InheritedTrim", "first", "last")]
    public void WhitespaceMetadataIsInherited(string type, string first, string last)
    {
        var root = (InheritedWhitespace)CompileAndRun("<InheritedWhitespace" + Namespace + "> first <" + type + "/> last </InheritedWhitespace>");
        Assert.Equal(3, root.Count);
        Assert.Equal(first, root[0]);
        Assert.Equal(last, root[2]);
    }

    [Fact]
    public void TypeConvertersDoNotInheritImplicitly()
    {
        Assert.ThrowsAny<XamlParseException>(() => Compile("<DerivedConversionContainer" + Namespace + " Value='hello'/>"));
    }

    [Theory]
    [InlineData("Number", 42)]
    [InlineData("ConverterMetadataBase.Number", -42)]
    public void OverriddenPropertiesUseTheSelectedDeclarationsConverter(string member, int expected)
    {
        var root = (ConverterMetadataDerived)CompileAndRun("<ConverterMetadataDerived" + Namespace + " " + member + "='42'/>");
        Assert.Equal(expected, root.Number);
    }
}

[UsableDuringInitialization(true)]
public class InitializationMetadataBase : ISupportInitialize
{
    public bool Initialized { get; private set; }
    public void BeginInit() => Initialized = false;
    public void EndInit() => Initialized = true;
}

public sealed class InheritedInitialization : InitializationMetadataBase { }

[UsableDuringInitialization(false)]
public sealed class OverriddenInitialization : InitializationMetadataBase { }

public sealed class InitializationRecorder
{
    public bool InitializedWhenAdded { get; private set; }
    public InitializationMetadataBase? Item { get; private set; }
    public void Add(InitializationMetadataBase item) { InitializedWhenAdded = item.Initialized; Item = item; }
}

[WhitespaceSignificantCollection]
public class WhitespaceMetadataBase : List<object> { }
public sealed class InheritedWhitespace : WhitespaceMetadataBase { }
public sealed class PlainMetadataItem { }
[TrimSurroundingWhitespace]
public class TrimMetadataBase { }
public sealed class InheritedTrim : TrimMetadataBase { }

public sealed class DerivedConvertedReference : ConvertedReference
{
    public DerivedConvertedReference() : base("constructed") { }
}
public sealed class DerivedConversionContainer
{
    public DerivedConvertedReference? Value { get; set; }
}

public class ConverterMetadataBase
{
    [TypeConverter(typeof(MetadataNumberConverter))]
    public virtual int Number { get; set; }
}
public sealed class ConverterMetadataDerived : ConverterMetadataBase
{
    public override int Number { get; set; }
}
public sealed class MetadataNumberConverter : TypeConverter
{
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => -int.Parse((string)value, culture);
}
