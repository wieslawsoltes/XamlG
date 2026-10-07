using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Metadata;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class ListLiteralTests
{
    [AvaloniaTheory]
    [InlineData("Array", "1, 2,3")]
    [InlineData("Array", ",1,,2,3,")]
    [InlineData("List", "1,2,3")]
    [InlineData("ReadOnly", "1,2,3")]
    [InlineData("Native", "1,2,3")]
    [InlineData("Native", ",1,,2,3,")]
    [InlineData("Native", "")]
    [InlineData("List", "   ")]
    [InlineData("ReadOnlyValues", "")]
    [InlineData("ReadOnlyStrings", "   ")]
    [InlineData("Strings", " one, ,two,,three ")]
    [InlineData("Annotated", " one; ;two||three ")]
    [InlineData("Inherited", " one; ;two||three ")]
    [InlineData("NoOptions", " one; ;two;;three ")]
    [InlineData("RemoveEmpty", " one; ;two;;three ")]
    [InlineData("WhitespaceSeparators", "one two\tthree")]
    [InlineData("NullSeparators", "one two\tthree")]
    [InlineData("Points", "1,2 3,4")]
    [InlineData("Points", "1 2,3 4")]
    [InlineData("Points", "1,\t2,3,\t4")]
    [InlineData("PointArray", "1,2 3,4")]
    [InlineData("DecoratedPoints", "1,2 3,4")]
    [InlineData("Brushes", "Red,#123456")]
    [InlineData("Rows", "Auto,2*")]
    [InlineData("Definitions", "Auto,2* 3")]
    [InlineData("Columns", "Auto 2*,3")]
    [InlineData("Trimming", "none,leadingcharacterellipsis")]
    public void CollectionShapeValuesAndMetadataMatchThePinnedCompiler(string property, string literal)
    {
        var xaml = Root(property, literal);
        var expected = Read(StructuredLiteralTests.Baseline(xaml), property);
        var actual = Read(AvaloniaCompilation.Build(xaml, true, StructuredLiteralTests.Path), property);
        Equivalent(expected, actual);
    }

    [AvaloniaTheory]
    [InlineData("Array", "1,2 3")]
    [InlineData("List", "1 2")]
    [InlineData("Native", "1, ,2")]
    [InlineData("Points", "1,2,3")]
    [InlineData("Points", "1\t2 3\t4")]
    [InlineData("DecoratedPoints", "1;2;3;4")]
    [InlineData("MutableBrushes", "Red,Blue")]
    [InlineData("NoDefaultConstructor", "1,2")]
    [InlineData("Definitions", "Auto;2*")]
    public void InvalidListContractsFailBinding(string property, string literal)
    {
        var xaml = Root(property, literal).Replace("\t", "&#x9;", StringComparison.Ordinal);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml, true, StructuredLiteralTests.Path).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Structured.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LiteralCollectionsEvaluateEachItemInOrderWithoutConstructionMetadata(bool metadata, bool array)
    {
        var xaml = Root(array ? "ParsedArray" : "ParsedList", "one,two");
        foreach (var native in new[] { false, true })
        {
            LiteralListEvents.Values.Clear();
            var value = native ? AvaloniaCompilation.Build(xaml, metadata, StructuredLiteralTests.Path) : StructuredLiteralTests.Baseline(xaml, metadata);
            var items = Assert.IsAssignableFrom<IEnumerable<ParsedLiteralItem>>(Read(value, array ? "ParsedArray" : "ParsedList"));
            Assert.Equal(new[] { "one", "two" }, items.Select(item => item.Text));
            Assert.All(items, item => Assert.Null(SourceInfo.GetXamlSourceInfo(item)));
            Assert.Null(SourceInfo.GetXamlSourceInfo(items));
            Assert.Equal(array ? new[] { "parse:one", "parse:two" } :
                new[] { "list", "parse:one", "add:one:2", "parse:two", "add:two:2" }, LiteralListEvents.Values);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredListConvertersEvaluateInOrderWithoutConstructionMetadata(bool metadata)
    {
        var xaml = "<ResourceDictionary " + StructuredLiteralTests.Ns + "><t:LiteralItemList x:Key='item'>one,two</t:LiteralItemList></ResourceDictionary>";
        foreach (var native in new[] { false, true })
        {
            LiteralListEvents.Values.Clear();
            var root = Assert.IsType<ResourceDictionary>(native ? AvaloniaCompilation.Build(xaml, metadata, StructuredLiteralTests.Path) : StructuredLiteralTests.Baseline(xaml, metadata));
            Assert.Empty(LiteralListEvents.Values);
            var items = Assert.IsType<LiteralItemList>(root["item"]);
            Assert.Equal(new[] { "one", "two" }, items.Select(item => item.Text));
            Assert.All(items, item => Assert.Null(item.ConverterSource));
            Assert.Null(SourceInfo.GetXamlSourceInfo(items));
            Assert.Equal(new[] { "list", "converter", "convert:one", "add:one:2", "converter", "convert:two", "add:two:2" }, LiteralListEvents.Values);
        }
    }

    [AvaloniaTheory]
    [InlineData("<t:AnnotatedLiteralStrings x:Key='item'>one;two</t:AnnotatedLiteralStrings>")]
    [InlineData("<t:LiteralItemList x:Key='item'>one,two</t:LiteralItemList>")]
    public void ResourceListsRecordTheKeyLocationBeforeRealizingTheirValues(string entry)
    {
        var xaml = "<ResourceDictionary " + StructuredLiteralTests.Ns + ">\n  " + entry + "\n</ResourceDictionary>";
        var expected = Assert.IsType<ResourceDictionary>(StructuredLiteralTests.Baseline(xaml));
        var actual = Assert.IsType<ResourceDictionary>(AvaloniaCompilation.Build(xaml, true, StructuredLiteralTests.Path));
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, "item"), SourceInfo.GetXamlSourceInfo(actual, "item"));
        Equivalent(expected["item"], actual["item"]);
    }

    private static string Root(string property, string literal) => "<t:ListLiteralProbe " + StructuredLiteralTests.Ns +
        "\n  " + property + "='" + StructuredLiteralTests.Escape(literal) + "'/>";
    private static object? Read(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value);
    private static void Equivalent(object? expected, object? actual)
    {
        if (expected == null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(SourceInfo.GetXamlSourceInfo(expected), SourceInfo.GetXamlSourceInfo(actual));
        if (expected.GetType().GetProperty("Capacity") is { } capacity)
            Assert.Equal(capacity.GetValue(expected), capacity.GetValue(actual));
        switch (expected)
        {
            case string: Assert.Equal(expected, actual); break;
            case IEnumerable items:
                var other = Assert.IsAssignableFrom<IEnumerable>(actual).Cast<object?>().ToArray();
                var values = items.Cast<object?>().ToArray();
                Assert.Equal(values.Length, other.Length);
                for (var i = 0; i < values.Length; i++) Equivalent(values[i], other[i]);
                break;
            case ISolidColorBrush brush: Assert.Equal(brush.Color, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color); break;
            case RowDefinition row: Assert.Equal(row.Height, Assert.IsType<RowDefinition>(actual).Height); break;
            case ColumnDefinition column: Assert.Equal(column.Width, Assert.IsType<ColumnDefinition>(actual).Width); break;
            default: Assert.Equal(expected, actual); break;
        }
    }
}

public sealed class ListLiteralProbe
{
    public double[]? Array { get; set; }
    public IList<double>? List { get; set; }
    public IReadOnlyList<double>? ReadOnly { get; set; }
    public AvaloniaList<double>? Native { get; set; }
    public IList<double> ReadOnlyValues { get; } = new List<double> { 99 };
    public IList<string> ReadOnlyStrings { get; } = new List<string> { "initial" };
    public IList<string>? Strings { get; set; }
    public AnnotatedLiteralStrings? Annotated { get; set; }
    public InheritedLiteralStrings? Inherited { get; set; }
    public NoOptionsLiteralStrings? NoOptions { get; set; }
    public RemoveEmptyLiteralStrings? RemoveEmpty { get; set; }
    public WhitespaceLiteralStrings? WhitespaceSeparators { get; set; }
    public NullSeparatorLiteralStrings? NullSeparators { get; set; }
    public IList<Point>? Points { get; set; }
    public Point[]? PointArray { get; set; }
    public DecoratedLiteralPoints? DecoratedPoints { get; set; }
    public IList<IBrush>? Brushes { get; set; }
    public IList<SolidColorBrush>? MutableBrushes { get; set; }
    public RowDefinition[]? Rows { get; set; }
    public RowDefinitions? Definitions { get; set; }
    public ColumnDefinitions? Columns { get; set; }
    public IList<TextTrimming>? Trimming { get; set; }
    public NoDefaultLiteralList? NoDefaultConstructor { get; set; }
    public ParsedLiteralItem[]? ParsedArray { get; set; }
    public ParsedLiteralList? ParsedList { get; set; }
}

[AvaloniaList(Separators = new[] { ";", "|" })]
public class AnnotatedLiteralStrings : AvaloniaList<string>;
public sealed class InheritedLiteralStrings : AnnotatedLiteralStrings;
[AvaloniaList(Separators = new[] { ";" }, SplitOptions = StringSplitOptions.None)]
public sealed class NoOptionsLiteralStrings : AvaloniaList<string>;
[AvaloniaList(Separators = new[] { ";" }, SplitOptions = StringSplitOptions.RemoveEmptyEntries)]
public sealed class RemoveEmptyLiteralStrings : AvaloniaList<string>;
[AvaloniaList(Separators = null, SplitOptions = StringSplitOptions.RemoveEmptyEntries)]
public sealed class WhitespaceLiteralStrings : AvaloniaList<string>;
[AvaloniaList(Separators = new string[] { null! })]
public sealed class NullSeparatorLiteralStrings : AvaloniaList<string>;
[AvaloniaList(Separators = new[] { ";" })]
public sealed class DecoratedLiteralPoints : AvaloniaList<Point>;
public sealed class NoDefaultLiteralList(int count) : AvaloniaList<double>(count);

public static class LiteralListEvents
{
    public static List<string> Values { get; } = new();
}

[TypeConverter(typeof(LiteralListItemConverter))]
public sealed record LiteralListItem(string Text, SourceInfo? ConverterSource);
public sealed class LiteralListItemConverter : TypeConverter
{
    public LiteralListItemConverter() => LiteralListEvents.Values.Add("converter");
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        LiteralListEvents.Values.Add("convert:" + value);
        return new LiteralListItem((string)value, SourceInfo.GetXamlSourceInfo(this));
    }
}
public sealed class LiteralItemList : AvaloniaList<LiteralListItem>
{
    public LiteralItemList() => LiteralListEvents.Values.Add("list");
    public override void Add(LiteralListItem item)
    {
        LiteralListEvents.Values.Add("add:" + item.Text + ":" + Capacity);
        base.Add(item);
    }
}
public sealed record ParsedLiteralItem(string Text)
{
    public static ParsedLiteralItem Parse(string text)
    { LiteralListEvents.Values.Add("parse:" + text); return new(text); }
}
public sealed class ParsedLiteralList : AvaloniaList<ParsedLiteralItem>
{
    public ParsedLiteralList() => LiteralListEvents.Values.Add("list");
    public override void Add(ParsedLiteralItem item)
    {
        LiteralListEvents.Values.Add("add:" + item.Text + ":" + Capacity);
        base.Add(item);
    }
}
