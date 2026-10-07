using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class LiteralNameContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    public static IEnumerable<object[]> LiteralNames()
    {
        foreach (var form in new[] { "Name", "x:Name", "String" })
            foreach (var name in new[] { "", " ", "with-dash", "with space", "1first", "class", "@escaped", "éclair", "名", "😀" })
                if (form != "String" || name.Length > 0)
                    yield return new object[] { form, name };
    }

    [AvaloniaTheory]
    [MemberData(nameof(LiteralNames))]
    public void LiteralNamesRetainTheirFrameworkValues(string form, string name)
    {
        var child = form == "String" ? "<TextBox><TextBox.Name><x:String>" + name + "</x:String></TextBox.Name></TextBox>"
            : "<TextBox " + form + "='" + name + "'/>";
        foreach (var panel in CompileBoth<StackPanel>("<StackPanel " + Ns + ">" + child + "</StackPanel>"))
        {
            var target = Assert.IsType<TextBox>(Assert.Single(panel.Children));
            var omitted = form == "String" && name == " ";
            Assert.Equal(omitted ? null : name, target.Name);
            Assert.Equal(!omitted, ReferenceEquals(target, NameScope.GetNameScope(panel)!.Find(name)));
        }
    }

    [AvaloniaTheory]
    [InlineData("Name", "with-dash")]
    [InlineData("Name", "with space")]
    [InlineData("Name", "1first")]
    [InlineData("Name", "class")]
    [InlineData("x:Name", "with-dash")]
    [InlineData("x:Name", "with space")]
    [InlineData("x:Name", "1first")]
    [InlineData("x:Name", "class")]
    public void NonIdentifierNamesDeclareForwardBindingSources(string form, string name)
    {
        var xaml = "<StackPanel " + Ns + "><TextBlock Text=\"{CompiledBinding Text, ElementName='" + name + "'}\"/><TextBox " + form + "='" + name + "' Text='source value'/></StackPanel>";
        foreach (var panel in CompileBoth<StackPanel>(xaml))
            Assert.Equal("source value", Assert.IsType<TextBlock>(panel.Children[0]).Text);
    }

    [AvaloniaTheory]
    [InlineData("{t:ProvidedName}", "provided", true)]
    [InlineData("{t:BoxedProvidedName}", "provided", false)]
    [InlineData("{x:Static t:ProvidedNameValues.Text}", "provided", true)]
    [InlineData("{x:Static t:ProvidedNameValues.Boxed}", "provided", false)]
    [InlineData("{x:Null}", null, false)]
    [InlineData("{}{literal}", "{literal}", true)]
    public void NameDirectivesUseOrdinaryPropertyConversion(string value, string? name, bool registered)
    {
        foreach (var target in CompileBoth<TextBox>("<TextBox " + Ns + " x:Name='" + value + "'/>"))
        {
            Assert.Equal(name, target.Name);
            Assert.Equal(registered, ReferenceEquals(target, NameScope.GetNameScope(target)!.Find(name ?? "provided")));
        }
    }

    [AvaloniaTheory]
    [InlineData("x:Name='provided' Observed='{t:FindProvidedName}'", true)]
    [InlineData("Observed='{t:FindProvidedName}' x:Name='provided'", false)]
    public void NameDirectiveAssignmentAndRegistrationRetainAttributeOrder(string assignments, bool observed)
    {
        foreach (var target in CompileBoth<NormalizingNamedObject>("<t:NormalizingNamedObject " + Ns + " Scope='{t:ProvidedNameScope}' " + assignments + "/>"))
        {
            Assert.Equal("PROVIDED", target.Name);
            Assert.Equal(1, target.Sets);
            Assert.Equal(observed, ReferenceEquals(target, target.Observed));
            Assert.Same(target, target.Scope!.Find("provided"));
        }
    }

    [AvaloniaTheory]
    [InlineData("UnrelatedNameObject")]
    [InlineData("InheritedNameObject")]
    public void NameDirectivesDoNotRegisterUnrelatedNameProperties(string type)
    {
        foreach (var target in CompileBoth<UnrelatedNameObject>("<t:" + type + " " + Ns + " Scope='{t:ProvidedNameScope}' x:Name='provided'/>"))
        {
            Assert.Equal("provided", target.Name);
            Assert.Null(target.Scope!.Find("provided"));
        }
    }

    [AvaloniaFact]
    public void NameDirectivesRequireANameProperty()
    {
        var xaml = "<t:SetterHost " + Ns + " x:Name='provided'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("Name")]
    [InlineData("x:Name")]
    public void AssignableNameLiteralsDoNotInvokePropertyConverters(string form)
    {
        foreach (var target in CompileBoth<ConvertedLiteralNameObject>("<t:ConvertedLiteralNameObject " + Ns + " Scope='{t:ProvidedNameScope}' " + form + "='provided'/>"))
        {
            Assert.Equal("provided", target.Name);
            Assert.Same(target, target.Scope!.Find("provided"));
        }
    }

    [AvaloniaTheory]
    [InlineData("Name")]
    [InlineData("x:Name")]
    public void NonStringNameAssignmentsDoNotDeclareOrRegisterNames(string form)
    {
        foreach (var target in CompileBoth<NumericLiteralNameObject>("<t:NumericLiteralNameObject " + Ns + " Scope='{t:ProvidedNameScope}' " + form + "='42'/>"))
        {
            Assert.Equal(42, target.Name);
            Assert.Null(target.Scope!.Find("42"));
        }
    }

    [AvaloniaTheory]
    [InlineData("x:Name='first' Name='second'", "second")]
    [InlineData("Name='first' x:Name='second'", "second")]
    [InlineData("x:Name='first' Name='first'", "first")]
    [InlineData("Name='first' x:Name='first'", "first")]
    public void NameAndDirectiveAssignmentsKeepTheirIndividualRegistrations(string assignments, string finalName)
    {
        foreach (var target in CompileBoth<TextBox>("<TextBox " + Ns + " " + assignments + "/>"))
        {
            Assert.Equal(finalName, target.Name);
            var scope = NameScope.GetNameScope(target)!;
            Assert.Same(target, scope.Find("first"));
            Assert.Same(target, scope.Find(finalName));
        }
    }

    [AvaloniaTheory]
    [InlineData("Name")]
    [InlineData("x:Name")]
    public void DuplicateLiteralNamesFailWhenTheFrameworkRegistersTheSecondObject(string form)
    {
        var xaml = "<StackPanel " + Ns + "><TextBox " + form + "='duplicate'/><TextBox " + form + "='duplicate'/></StackPanel>";
        Assert.IsType<ArgumentException>(Unwrap(AvaloniaUpstreamCompilation.Compile(xaml).Error));
        var fixture = new ResourceProjectFixture(new[] { ("Names.axaml", xaml) });
        Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
        Assert.IsType<ArgumentException>(Unwrap(Record.Exception(() => fixture.Build("Names.axaml"))));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateSourceMetadataUsesTheFirstDeclarationBeforeRuntimeValidation(bool textBoxFirst)
    {
        var sources = textBoxFirst ? "<TextBox Name='duplicate'/><Button Name='duplicate'/>" : "<Button Name='duplicate'/><TextBox Name='duplicate'/>";
        var xaml = "<StackPanel " + Ns + ">" + sources + "<TextBlock Text='{CompiledBinding Text, ElementName=duplicate}'/></StackPanel>";
        var error = Unwrap(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var fixture = new ResourceProjectFixture(new[] { ("Names.axaml", xaml) });
        if (textBoxFirst)
        {
            Assert.True(error is ArgumentException, error?.ToString());
            Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
            Assert.IsType<ArgumentException>(Unwrap(Record.Exception(() => fixture.Build("Names.axaml"))));
        }
        else
        {
            Assert.NotNull(error);
            Assert.IsNotType<ArgumentException>(error);
            Assert.False(fixture.Result.Success);
        }
    }

    [AvaloniaTheory]
    [InlineData(" ", true, " ")]
    [InlineData("  first  second  ", false, "first second")]
    [InlineData("  first  second  ", true, null)]
    public void StringElementNamesRetainTheirWhitespaceMode(string text, bool preserve, string? expected)
    {
        expected ??= text;
        var xaml = "<TextBox " + Ns + "><TextBox.Name><x:String" + (preserve ? " xml:space='preserve'" : "") + ">" + text + "</x:String></TextBox.Name></TextBox>";
        foreach (var target in CompileBoth<TextBox>(xaml))
        {
            Assert.Equal(expected, target.Name);
            Assert.Same(target, NameScope.GetNameScope(target)!.Find(expected));
        }
    }

    [AvaloniaFact]
    public void BothLiteralNamesDeclareForwardBindingSources()
    {
        var xaml = "<StackPanel " + Ns + "><TextBlock Text='{CompiledBinding Text, ElementName=first}'/><TextBlock Text='{CompiledBinding Text, ElementName=second}'/><TextBox Name='first' x:Name='second' Text='source value'/></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
            Assert.All(root.Children.OfType<TextBlock>(), block => Assert.Equal("source value", block.Text));
    }

    [AvaloniaFact]
    public void NativeEmptyStringElementsRemainSupported()
    {
        var xaml = "<TextBox " + Ns + "><TextBox.Name><x:String/></TextBox.Name></TextBox>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var target = Assert.IsType<TextBox>(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Build("Names.axaml"));
        Assert.Equal("", target.Name);
        Assert.Same(target, NameScope.GetNameScope(target)!.Find(""));
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("with-dash")]
    [InlineData("with space")]
    [InlineData("@escaped")]
    public void NativeCodeBehindRetainsRuntimeNamesWithoutInvalidFields(string name)
    {
        var xaml = "<StackPanel " + Ns + " x:Class='Demo.NamedView'><TextBox x:Name='" + name + "'/><TextBox Name='class' x:Name='alias'/></StackPanel>";
        var fixture = new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }, sourceCode: "namespace Demo; public partial class NamedView : Avalonia.Controls.StackPanel { }");
        var root = Assert.IsAssignableFrom<StackPanel>(fixture.Build("Names.axaml"));
        Assert.Same(root.Children[0], NameScope.GetNameScope(root)!.Find(name));
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Assert.Null(root.GetType().GetField(name, flags));
        Assert.Same(root.Children[1], root.GetType().GetField("class", flags)!.GetValue(root));
        Assert.Same(root.Children[1], root.GetType().GetField("alias", flags)!.GetValue(root));
    }

    private static Exception? Unwrap(Exception? error)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        return error;
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Build("Names.axaml"));
    }
}

public sealed class ConvertedLiteralNameObject : INamed
{
    public INameScope? Scope { get; set; }
    [System.ComponentModel.TypeConverter(typeof(SelectorRejectedMemberConverter))]
    public string? Name { get; set; }
}

public sealed class NumericLiteralNameObject : INamed
{
    public INameScope? Scope { get; set; }
    public int Name { get; set; }
    string? INamed.Name => Name.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
