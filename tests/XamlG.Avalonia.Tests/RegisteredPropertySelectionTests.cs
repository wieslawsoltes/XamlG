using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RegisteredPropertySelectionTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadOnlyWrappersUseTheStaticUnsetSetterWithoutEvaluatingItsProvider(bool inTemplate, bool objectForm)
    {
        var properties = new[] { "Value", "ObjectValue", "Assigned", "DirectValue" };
        var attributes = objectForm ? "" : string.Join(" ", properties.Select(name => name + "='{t:TypedUnset}'"));
        var content = objectForm ? string.Concat(properties.Select(name => "<t:ReadOnlyWrapperProbe." + name + "><t:TypedUnsetExtension/></t:ReadOnlyWrapperProbe." + name + ">")) : "";
        foreach (var root in CompileBoth(Xaml("t:ReadOnlyWrapperProbe", attributes, content, inTemplate)))
        {
            var control = Assert.IsType<ReadOnlyWrapperProbe>(Realize(root));
            Assert.Equal(17d, control.Value);
            Assert.Equal("style", control.ObjectValue);
            Assert.Equal("style", Assert.IsType<ReflectionBinding>(control.Assigned).Path);
            Assert.Equal(-7, control.DirectValue);
            Assert.Equal(0, UnsetProviderCalls.Constructions);
            Assert.Equal(0, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData("Value", false)]
    [InlineData("ObjectValue", false)]
    [InlineData("DirectValue", false)]
    [InlineData("DirectValue", true)]
    public void BoxedUnsetClearsReadOnlyWrappersWithAnUnsetAlternative(string property, bool inTemplate)
    {
        foreach (var root in CompileBoth(Xaml("t:ReadOnlyWrapperProbe", property + "='{t:BoxedUnset}'", "", inTemplate)))
        {
            var control = Assert.IsType<ReadOnlyWrapperProbe>(Realize(root));
            if (property == "Value") Assert.Equal(17d, control.Value);
            else if (property == "ObjectValue") Assert.Equal("style", control.ObjectValue);
            else Assert.Equal(-7, control.DirectValue);
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData("Value")]
    [InlineData("ObjectValue")]
    public void ReadOnlyTemplateAlternativesDiscardUnsetWhenBindingPriorityIsAvailable(string property)
    {
        foreach (var root in CompileBoth(Xaml("t:ReadOnlyWrapperProbe", property + "='{t:BoxedUnset}'", "", true)))
        {
            Assert.Throws<InvalidCastException>(() => Realize(root));
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "{t:BoxedUnset}")]
    [InlineData(true, "{t:BoxedUnset}")]
    [InlineData(false, "{t:BoxedNameBinding}")]
    [InlineData(true, "{t:BoxedNameBinding}")]
    public void ReadOnlyAssignBindingLeavesOnlyTheUnsetAlternativeForObjectProviders(bool inTemplate, string value)
    {
        foreach (var root in CompileBoth(Xaml("t:ReadOnlyWrapperProbe", "Assigned='" + value + "'", "", inTemplate)))
        {
            var control = Assert.IsType<ReadOnlyWrapperProbe>(Realize(root));
            Assert.Equal("style", Assert.IsType<ReflectionBinding>(control.Assigned).Path);
            Assert.Equal(0, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReadOnlyWrappersAcceptProvidedBindingObjects(bool inTemplate, bool objectForm)
    {
        var attribute = objectForm ? "" : "Text='{t:BoxedNameBinding}'";
        var content = objectForm ? "<t:ReadOnlyWrapperProbe.Text><t:BoxedNameBindingExtension/></t:ReadOnlyWrapperProbe.Text>" : "";
        foreach (var root in CompileBoth(Xaml("t:ReadOnlyWrapperProbe", attribute, content, inTemplate)))
        {
            var control = Assert.IsType<ReadOnlyWrapperProbe>(Realize(root));
            var model = new BindingFixtureModel { Name = "first" };
            control.DataContext = model;
            Assert.Equal("first", control.Text);
            model.Name = "second";
            Assert.Equal("second", control.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "{t:BoxedTemplateNumber}", typeof(InvalidCastException))]
    [InlineData(true, "{t:BoxedTemplateNumber}", typeof(InvalidCastException))]
    [InlineData(false, "{t:RegisteredNull}", typeof(NullReferenceException))]
    [InlineData(true, "{t:RegisteredNull}", typeof(NullReferenceException))]
    public void ReadOnlyRuntimeAlternativesRejectUnmatchedValues(bool inTemplate, string value, Type errorType)
    {
        var xaml = Xaml("t:ReadOnlyWrapperProbe", "Value='" + value + "'", "", inTemplate);
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        var baselineError = baseline.Error ?? Record.Exception(() => Realize(baseline.Root!));
        Assert.NotNull(baselineError);
        Assert.IsType(errorType, Unwrap(baselineError));
        var actualError = Record.Exception(() => Realize(new ResourceProjectFixture(new[] { ("Invalid.axaml", xaml) }).Build("Invalid.axaml")));
        Assert.NotNull(actualError);
        Assert.IsType(errorType, Unwrap(actualError));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImplicitContentDoesNotAcquireTheUnsetProviderOptimization(bool inTemplate)
    {
        foreach (var root in CompileBoth(Xaml("ContentControl", "", "<t:TypedUnsetExtension/>", inTemplate)))
        {
            Assert.Null(Assert.IsType<ContentControl>(Realize(root)).Content);
            Assert.Equal(1, UnsetProviderCalls.Constructions);
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImplicitContentRetainsBindingObjectsAsValues(bool inTemplate)
    {
        foreach (var root in CompileBoth(Xaml("ContentControl", "", "<ReflectionBinding Path='Name'/>", inTemplate)))
            Assert.Equal("Name", Assert.IsType<ReflectionBinding>(Assert.IsType<ContentControl>(Realize(root)).Content).Path);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void QualifiedPropertyUsesItsDeclaringRegistration(bool objectForm)
    {
        var attribute = objectForm ? "" : "t:BaseRegistrationProbe.Value='{t:TypedUnset}'";
        var content = objectForm ? "<t:BaseRegistrationProbe.Value><t:TypedUnsetExtension/></t:BaseRegistrationProbe.Value>" : "";
        foreach (var root in CompileBoth(Xaml("t:HiddenRegistrationProbe", attribute, content, false)))
        {
            var control = Assert.IsType<HiddenRegistrationProbe>(root);
            Assert.Equal(17d, ((BaseRegistrationProbe)control).Value);
            Assert.Equal(43d, control.Value);
        }
    }

    [AvaloniaFact]
    public void QualifiedTemplatePropertyWritesItsDeclaringRegistration()
    {
        foreach (var root in CompileBoth(Xaml("t:HiddenRegistrationProbe", "t:BaseRegistrationProbe.Value='7'", "", true)))
        {
            var control = Assert.IsType<HiddenRegistrationProbe>(Realize(root));
            control.ClearValue(BaseRegistrationProbe.ValueProperty);
            Assert.Equal(7d, ((BaseRegistrationProbe)control).Value);
            Assert.Equal(43d, control.Value);
        }
    }

    [AvaloniaFact]
    public void NativePrivateWrapperExtensionConvertsTypedStringProviders()
    {
        var xaml = Xaml("t:ReadOnlyWrapperProbe", "Value='{t:RegisteredString}'", "", false);
        Assert.Equal(42d, Assert.IsType<ReadOnlyWrapperProbe>(new ResourceProjectFixture(new[] { ("Native.axaml", xaml) }).Build("Native.axaml")).Value);
    }

    private static string Xaml(string type, string attributes, string content, bool inTemplate) => inTemplate
        ? "<ControlTemplate " + Ns + " TargetType='Button'><" + type + " " + attributes + ">" + content + "</" + type + "></ControlTemplate>"
        : "<" + type + " " + Ns + " " + attributes + ">" + content + "</" + type + ">";

    private static object Realize(object root) => root is IControlTemplate template ? template.Build(new Button())!.Result : root;

    private static Exception Unwrap(Exception error)
    {
        while (error is System.Reflection.TargetInvocationException { InnerException: { } inner }) error = inner;
        return error;
    }

    private static IEnumerable<object> CompileBoth(string xaml)
    {
        UnsetProviderCalls.Constructions = UnsetProviderCalls.Provides = 0;
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<object>(baseline.Root);
        UnsetProviderCalls.Constructions = UnsetProviderCalls.Provides = 0;
        yield return new ResourceProjectFixture(new[] { ("Selection.axaml", xaml) }).Build("Selection.axaml");
    }
}

public sealed class RegisteredNullExtension { public object? ProvideValue() => null; }

public sealed class ReadOnlyWrapperProbe : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ReadOnlyWrapperProbe, double>(nameof(Value));
    public static readonly StyledProperty<object?> ObjectValueProperty = AvaloniaProperty.Register<ReadOnlyWrapperProbe, object?>(nameof(ObjectValue));
    public static readonly StyledProperty<BindingBase?> AssignedProperty = AvaloniaProperty.Register<ReadOnlyWrapperProbe, BindingBase?>(nameof(Assigned));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ReadOnlyWrapperProbe, string?>(nameof(Text));
    public static readonly DirectProperty<ReadOnlyWrapperProbe, int> DirectValueProperty = AvaloniaProperty.RegisterDirect<ReadOnlyWrapperProbe, int>(nameof(DirectValue), target => target.DirectValue, (target, value) => target.SetAndRaise(DirectValueProperty!, ref target._directValue, value), unsetValue: -7);
    private int _directValue = 23;

    public ReadOnlyWrapperProbe()
    {
        SetValue(ValueProperty, 17d, BindingPriority.Style); SetValue(ValueProperty, 41d);
        SetValue(ObjectValueProperty, "style", BindingPriority.Style); SetValue(ObjectValueProperty, "local");
        SetValue(AssignedProperty, new ReflectionBinding("style"), BindingPriority.Style);
        SetValue(AssignedProperty, new ReflectionBinding("local"));
    }
    public double Value => GetValue(ValueProperty);
    public object? ObjectValue => GetValue(ObjectValueProperty);
    [AssignBinding] public BindingBase? Assigned => GetValue(AssignedProperty);
    public string? Text => GetValue(TextProperty);
    public int DirectValue => _directValue;
}

public class BaseRegistrationProbe : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<BaseRegistrationProbe, double>(nameof(Value));
    public BaseRegistrationProbe() { SetValue(ValueProperty, 17d, BindingPriority.Style); SetValue(ValueProperty, 41d); }
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}

public sealed class HiddenRegistrationProbe : BaseRegistrationProbe
{
    public new static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<HiddenRegistrationProbe, double>(nameof(Value));
    public HiddenRegistrationProbe() => SetValue(ValueProperty, 43d);
    public new double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
}
