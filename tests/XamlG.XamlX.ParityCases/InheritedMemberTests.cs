using System;
using System.Collections.Generic;
using Xunit;

namespace XamlParserTests.Parity;

[Trait("Category", "ParityRegression")]
public sealed class InheritedMemberTests : CompilerTestBase
{
    private const string Namespace = " xmlns='clr-namespace:XamlParserTests.Parity;assembly=XamlParserTests' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    [Theory]
    [InlineData("", "base")]
    [InlineData("", "<ContentBase.Text>base</ContentBase.Text>")]
    [InlineData("ContentBase.Text='base'", "")]
    public void InheritedContentAndQualifiedPropertiesRetainTheirDeclaringMember(string attributes, string content)
    {
        var root = (HiddenContent)CompileAndRun("<HiddenContent" + Namespace + " " + attributes + ">" + content + "</HiddenContent>");
        Assert.Equal("base", ((ContentBase)root).Text);
        Assert.Null(root.Text);
    }

    [Fact]
    public void UnqualifiedPropertiesUseTheDerivedMember()
    {
        var root = (HiddenContent)CompileAndRun("<HiddenContent" + Namespace + " Text='derived'/>");
        Assert.Equal("derived", root.Text);
        Assert.Null(((ContentBase)root).Text);
    }

    [Theory]
    [InlineData("<x:String>base</x:String>")]
    [InlineData("<CollectionBase.Items><x:String>base</x:String></CollectionBase.Items>")]
    [InlineData("<ObjectTextValue/>")]
    public void InheritedCollectionsRetainTheirDeclaredGetter(string content)
    {
        var root = (HiddenCollection)CompileAndRun("<HiddenCollection" + Namespace + ">" + content + "</HiddenCollection>");
        Assert.Equal(new[] { "base" }, ((CollectionBase)root).Items);
        Assert.Empty(root.Items);
    }

    [Theory]
    [InlineData("<ObjectTextValue/>")]
    [InlineData("<ObjectListValue/>")]
    public void RuntimeCollectionSettersAndAddersRetainTheirDeclaringMember(string content)
    {
        var root = (HiddenReplacement)CompileAndRun("<HiddenReplacement" + Namespace + ">" + content + "</HiddenReplacement>");
        Assert.Equal(new[] { "base" }, ((ReplacementBase)root).Items);
        Assert.Empty(root.Items);
    }

    [Fact]
    public void QualifiedEventsUseTheDeclaredAdder()
    {
        var root = (HiddenEvents)CompileAndRun("<HiddenEvents" + Namespace + " EventBase.Changed='Handle'/>");
        root.RaiseDerived();
        Assert.Equal(0, root.Calls);
        root.Raise();
        Assert.Equal(1, root.Calls);
    }
}

public class ContentBase
{
    [Content] public string? Text { get; set; }
}

public sealed class HiddenContent : ContentBase
{
    public new string? Text { get; set; }
}

public class CollectionBase
{
    [Content] public List<string> Items { get; } = new();
}

public sealed class HiddenCollection : CollectionBase
{
    public new List<int> Items { get; } = new();
}

public class ReplacementBase
{
    [Content] public List<string> Items { get; set; } = new();
}

public sealed class HiddenReplacement : ReplacementBase
{
    public new List<int> Items { get; set; } = new();
}

public sealed class ObjectTextValue
{
    public object ProvideValue() => "base";
}

public sealed class ObjectListValue
{
    public object ProvideValue() => new List<string> { "base" };
}

public class EventBase
{
    public event EventHandler? Changed;
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class HiddenEvents : EventBase
{
    public new event EventHandler? Changed;
    public int Calls { get; private set; }
    public void Handle(object? sender, EventArgs args) => Calls++;
    public void RaiseDerived() => Changed?.Invoke(this, EventArgs.Empty);
}
