using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using System.Reflection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ProvidedNameTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("Name='{t:ProvidedName}'", "", true, 1)]
    [InlineData("Name='{t:BoxedProvidedName}'", "", false, 1)]
    [InlineData("Name='{x:Static t:ProvidedNameValues.Text}'", "", true, 0)]
    [InlineData("Name='{x:Static t:ProvidedNameValues.Boxed}'", "", false, 0)]
    [InlineData("Name='{x:Static t:ProvidedNameValues.TextGetter}'", "", true, 1)]
    [InlineData("Name='{x:Static t:ProvidedNameValues.BoxedGetter}'", "", false, 1)]
    [InlineData("", "<TextBox.Name><t:ProvidedNameExtension/></TextBox.Name>", true, 1)]
    [InlineData("", "<TextBox.Name><t:BoxedProvidedNameExtension/></TextBox.Name>", false, 1)]
    [InlineData("", "<TextBox.Name><x:String>provided</x:String></TextBox.Name>", true, 0)]
    public void ProvidedNamesRegisterAccordingToTheirStaticValueType(string attribute, string content, bool registered, int calls)
    {
        var xaml = "<StackPanel " + Ns + "><TextBlock Text='{ReflectionBinding Text, ElementName=provided}'/><TextBox " + attribute + " Text='named value'>" + content + "</TextBox></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<TextBox>(root.Children[1]);
            Assert.Equal("provided", source.Name);
            var scope = NameScope.GetNameScope(root)!;
            Assert.Equal(registered, ReferenceEquals(source, scope.Find("provided")));
            Assert.Equal(registered ? "named value" : null, Assert.IsType<TextBlock>(root.Children[0]).Text);
            Assert.Equal(calls, ProvidedNameExtension.Calls);
        }
    }

    [AvaloniaTheory]
    [InlineData("Name='{t:ProvidedName}'")]
    [InlineData("Name='{x:Static t:ProvidedNameValues.Text}'")]
    public void RuntimeNamesDoNotDeclareCompiledBindingSources(string name)
    {
        var xaml = "<StackPanel " + Ns + "><TextBox " + name + "/><TextBlock Text='{CompiledBinding #provided.Text}'/></StackPanel>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("Name='{t:ProvidedName}'")]
    [InlineData("Name='provided'")]
    public void RegistrationFollowsTheSetterAndRetainsTheOriginalName(string name)
    {
        var xaml = "<StackPanel " + Ns + "><StackPanel.Resources><t:NormalizingNamedObject x:Key='item' Scope='{t:ProvidedNameScope}' " + name + " Observed='{t:FindProvidedName}'/></StackPanel.Resources></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
        {
            var source = Assert.IsType<NormalizingNamedObject>(root.Resources["item"]);
            Assert.False(source.RegisteredDuringSet);
            Assert.Equal("PROVIDED", source.Name);
            Assert.Same(source, source.Observed);
            Assert.Same(source, source.Scope!.Find("provided"));
            Assert.Null(source.Scope.Find("PROVIDED"));
            Assert.Equal(name == "Name='provided'", ReferenceEquals(source, NameScope.GetNameScope(root)!.Find("provided")));
            Assert.Equal(1, source.Sets);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvidedNamesBelongToEachTemplateInstance(bool nested)
    {
        var body = "<TextBox Name='{t:ProvidedName}' Text='template value'/>";
        if (nested) body = "<StackPanel>" + body + "</StackPanel>";
        foreach (var template in CompileBoth<IControlTemplate>("<ControlTemplate " + Ns + " TargetType='Button'>" + body + "</ControlTemplate>"))
        {
            var first = template.Build(new Button())!;
            var second = template.Build(new Button())!;
            var firstSource = Assert.IsType<TextBox>(first.NameScope.Find("provided"));
            var secondSource = Assert.IsType<TextBox>(second.NameScope.Find("provided"));
            Assert.NotSame(firstSource, secondSource);
            Assert.Equal(2, ProvidedNameExtension.Calls);
        }
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("with-dash")]
    [InlineData("with space")]
    public void RuntimeNamesNeedNotBeCSharpIdentifiers(string name)
    {
        foreach (var root in CompileBoth<TextBox>("<TextBox " + Ns + "><TextBox.Name><t:ProvidedNameExtension Value='" + name + "'/></TextBox.Name></TextBox>"))
        {
            Assert.Equal(name, root.Name);
            Assert.Same(root, NameScope.GetNameScope(root)!.Find(name));
        }
    }

    [AvaloniaFact]
    public void AStringElementNameDeclaresACompiledBindingSource()
    {
        var xaml = "<StackPanel " + Ns + "><TextBlock Text='{CompiledBinding #provided.Text}'/><TextBox Text='string element'><TextBox.Name><x:String>provided</x:String></TextBox.Name></TextBox></StackPanel>";
        foreach (var root in CompileBoth<StackPanel>(xaml))
            Assert.Equal("string element", Assert.IsType<TextBlock>(root.Children[0]).Text);
    }

    [AvaloniaFact]
    public void AnIntrinsicNullAssignsTheNameWithoutRegisteringIt()
    {
        foreach (var root in CompileBoth<TextBox>("<TextBox " + Ns + " Name='{x:Null}'/>"))
            Assert.Null(root.Name);
    }

    [AvaloniaTheory]
    [InlineData("{t:ProvidedName Value={x:Null}}")]
    [InlineData("{x:Static t:ProvidedNameValues.NullText}")]
    public void AStaticallyStringValuedNullFailsNameRegistration(string value)
    {
        AssertFailure<ArgumentNullException>("<TextBox " + Ns + " Name='" + value + "'/>");
    }

    [AvaloniaFact]
    public void DuplicateRuntimeNamesFailThroughTheFrameworkScope()
    {
        AssertFailure<ArgumentException>("<StackPanel " + Ns + "><TextBox Name='{t:ProvidedName}'/><TextBox Name='{t:ProvidedName}'/></StackPanel>");
    }

    [AvaloniaTheory]
    [InlineData("Name='{t:ProvidedName}'")]
    [InlineData("Name='provided'")]
    public void AFailedSetterDoesNotRegisterItsObject(string name)
    {
        var xaml = "<t:FailingNamedObject " + Ns + " Scope='{t:ProvidedNameScope}' " + name + "/>";
        AssertFailure<InvalidOperationException>(xaml, () =>
        {
            var source = FailingNamedObject.Last!;
            Assert.Null(source.Scope!.Find("provided"));
        });
    }

    [AvaloniaFact]
    public void InitOnlyNameSettersRetainRuntimeRegistration()
    {
        foreach (var root in CompileBoth<InitNamedObject>("<t:InitNamedObject " + Ns + " Scope='{t:ProvidedNameScope}' Name='{t:ProvidedName}'/>"))
        {
            Assert.Equal("provided", root.Name);
            Assert.Same(root, root.Scope!.Find("provided"));
            Assert.Equal(1, ProvidedNameExtension.Calls);
        }
    }

    [AvaloniaTheory]
    [InlineData("UnrelatedNameObject", "provided")]
    [InlineData("UnrelatedNameObject", "{t:ProvidedName}")]
    [InlineData("InheritedNameObject", "provided")]
    [InlineData("InheritedNameObject", "{t:ProvidedName}")]
    public void RegistrationRequiresTheDeclaringTypeToImplementNamed(string type, string name)
    {
        foreach (var root in CompileBoth<UnrelatedNameObject>("<t:" + type + " " + Ns + " Scope='{t:ProvidedNameScope}' Name='" + name + "'/>"))
        {
            Assert.Equal("provided", root.Name);
            Assert.Null(root.Scope!.Find("provided"));
        }
    }

    [AvaloniaFact]
    public void AStringAssignedToAnObjectPropertyCanRegisterTheName()
    {
        foreach (var root in CompileBoth<ObjectNameObject>("<t:ObjectNameObject " + Ns + " Scope='{t:ProvidedNameScope}' Name='{t:ProvidedName}'/>"))
        {
            Assert.Equal("provided", root.Name);
            Assert.Same(root, root.Scope!.Find("provided"));
        }
    }

    [AvaloniaFact]
    public void ValueTypeNameRegistrationPreservesTheAssignedValue()
    {
        var xaml = "<t:NamedValueObject " + Ns + " Scope='{t:ProvidedNameScope}' Name='{t:ProvidedName}'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var root = Assert.IsType<NamedValueObject>(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Build("Names.axaml"));
        Assert.Equal("provided", root.Name);
        Assert.Equal("provided", Assert.IsType<NamedValueObject>(root.Scope!.Find("provided")).Name);
    }

    private static void AssertFailure<T>(string xaml, Action? inspect = null) where T : Exception
    {
        Assert.IsType<T>(Unwrap(AvaloniaUpstreamCompilation.Compile(xaml).Error));
        inspect?.Invoke();
        var fixture = new ResourceProjectFixture(new[] { ("Names.axaml", xaml) });
        Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
        Assert.IsType<T>(Unwrap(Record.Exception(() => fixture.Build("Names.axaml"))));
        inspect?.Invoke();
    }

    private static Exception? Unwrap(Exception? error)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        return error;
    }

    private static IEnumerable<T> CompileBoth<T>(string xaml)
    {
        ProvidedNameExtension.Calls = 0;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        ProvidedNameExtension.Calls = 0;
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Names.axaml", xaml) }).Build("Names.axaml"));
    }
}

public sealed class ProvidedNameExtension
{
    public static int Calls { get; set; }
    public string? Value { get; set; } = "provided";
    public string? ProvideValue() { Calls++; return Value; }
}

public sealed class BoxedProvidedNameExtension
{
    public object ProvideValue() { ProvidedNameExtension.Calls++; return "provided"; }
}

public static class ProvidedNameValues
{
    public static readonly string Text = "provided";
    public static readonly object Boxed = "provided";
    public static readonly string? NullText = null;
    public static string TextGetter { get { ProvidedNameExtension.Calls++; return "provided"; } }
    public static object BoxedGetter { get { ProvidedNameExtension.Calls++; return "provided"; } }
}

public sealed class NormalizingNamedObject : INamed
{
    private string? _name;
    public INameScope? Scope { get; set; }
    public bool RegisteredDuringSet { get; private set; }
    public int Sets { get; private set; }
    public string? Name
    {
        get => _name;
        set { Sets++; RegisteredDuringSet = Scope!.Find(value!) != null; _name = value?.ToUpperInvariant(); }
    }
    public object? Observed { get; set; }
}

public sealed class ProvidedNameScopeExtension
{
    public INameScope ProvideValue(IServiceProvider services) => (INameScope)services.GetService(typeof(INameScope))!;
}

public sealed class FindProvidedNameExtension
{
    public object? ProvideValue(IServiceProvider services) => ((INameScope)services.GetService(typeof(INameScope))!).Find("provided");
}

public sealed class FailingNamedObject : INamed
{
    public FailingNamedObject() => Last = this;
    public static FailingNamedObject? Last { get; private set; }
    public INameScope? Scope { get; set; }
    public string? Name { get => null; set => throw new InvalidOperationException("Name setter failed."); }
}

public sealed class InitNamedObject : INamed
{
    public INameScope? Scope { get; set; }
    public string? Name { get; init; }
}

public class UnrelatedNameObject
{
    public INameScope? Scope { get; set; }
    public string? Name { get; set; }
}

public sealed class InheritedNameObject : UnrelatedNameObject, INamed { }

public sealed class ObjectNameObject : INamed
{
    public INameScope? Scope { get; set; }
    public object? Name { get; set; }
    string? INamed.Name => Name as string;
}

public struct NamedValueObject : INamed
{
    public INameScope? Scope { get; set; }
    public string? Name { get; set; }
}
