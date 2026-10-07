using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RegisteredSpecialValueTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' xmlns:a='clr-namespace:Avalonia;assembly=Avalonia.Base'";

    [AvaloniaTheory]
    [InlineData("{x:Static a:AvaloniaProperty.UnsetValue}")]
    [InlineData("{t:BoxedUnset}")]
    [InlineData("{t:TypedUnset}")]
    public void UnsetValuesClearRegisteredPropertiesWithoutUsingStyledClrSetters(string value)
    {
        var xaml = "<t:RegisteredSpecialValueProbe " + Ns + " Value='" + value + "' DirectValue='" + value + "' Tag='" + value + "' Grid.Row='" + value + "' Assigned='" + value + "'/>";
        foreach (var control in CompileBoth<RegisteredSpecialValueProbe>(xaml))
        {
            AssertCleared(control);
            Assert.Equal(0, control.Writes);
            Assert.Equal(1, control.DirectWrites);
        }
    }

    [AvaloniaTheory]
    [InlineData("<x:Static Member='a:AvaloniaProperty.UnsetValue'/>")]
    [InlineData("<t:BoxedUnsetExtension/>")]
    [InlineData("<t:TypedUnsetExtension/>")]
    public void ObjectFormUnsetValuesUseTheRegisteredSetter(string value)
    {
        var xaml = "<t:RegisteredSpecialValueProbe " + Ns + "><t:RegisteredSpecialValueProbe.Value>" + value + "</t:RegisteredSpecialValueProbe.Value></t:RegisteredSpecialValueProbe>";
        foreach (var control in CompileBoth<RegisteredSpecialValueProbe>(xaml))
        {
            Assert.Equal(17d, control.Value);
            Assert.Equal(0, control.Writes);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticallyTypedUnsetProvidersAreNotEvaluated(bool inTemplate)
    {
        var content = "<t:RegisteredSpecialValueProbe Value='{t:TypedUnset}'/>";
        var xaml = inTemplate ? "<ControlTemplate " + Ns + " TargetType='Button'>" + content + "</ControlTemplate>"
            : "<t:RegisteredSpecialValueProbe " + Ns + " Value='{t:TypedUnset}'/>";
        foreach (var root in CompileBoth<object>(xaml, Reset))
        {
            var control = Assert.IsType<RegisteredSpecialValueProbe>(root is IControlTemplate template ? template.Build(new Button())!.Result : root);
            Assert.Equal(17d, control.Value);
            Assert.Equal(0, UnsetProviderCalls.Constructions);
            Assert.Equal(0, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData("Value")]
    [InlineData("Grid.Row")]
    [InlineData("Assigned")]
    public void TemplateRuntimeAlternativesDoNotKeepTheUnsetSetter(string property)
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><t:RegisteredSpecialValueProbe " + property + "='{t:BoxedUnset}'/></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml, Reset))
        {
            Assert.Throws<InvalidCastException>(() => template.Build(new Button()));
            Assert.Equal(1, UnsetProviderCalls.Constructions);
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData("{t:BoxedUnset}", 1)]
    [InlineData("{t:TypedUnset}", 0)]
    public void DirectPropertyUnsettingWorksInsideTemplates(string value, int calls)
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><t:RegisteredSpecialValueProbe DirectValue='" + value + "'/></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml, Reset))
        {
            Assert.Equal(-7, Assert.IsType<RegisteredSpecialValueProbe>(template.Build(new Button())!.Result).DirectValue);
            Assert.Equal(calls, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaFact]
    public void BoxedUnsetOnAnObjectTemplatePropertyRetainsTheLocalValue()
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><t:RegisteredSpecialValueProbe Tag='{t:BoxedUnset}'/></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml))
            Assert.Equal("local", Assert.IsType<RegisteredSpecialValueProbe>(template.Build(new Button())!.Result).Tag);
    }

    [AvaloniaFact]
    public void TypedUnsetOnAnObjectTemplatePropertyIsEvaluatedAtTemplatePriority()
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><t:RegisteredSpecialValueProbe Tag='{t:TypedUnset}'/></ControlTemplate>";
        foreach (var template in CompileBoth<IControlTemplate>(xaml, Reset))
        {
            Assert.Equal("local", Assert.IsType<RegisteredSpecialValueProbe>(template.Build(new Button())!.Result).Tag);
            Assert.Equal(1, UnsetProviderCalls.Constructions);
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TypedStringProvidersAreConvertedBeforeRegisteredAssignment(bool inTemplate, bool objectForm)
    {
        var content = objectForm
            ? "<t:TemplatePriorityProbe.Value><t:RegisteredStringExtension/></t:TemplatePriorityProbe.Value>"
            : "";
        var attribute = objectForm ? "" : " Value='{t:RegisteredString}'";
        var xaml = inTemplate
            ? "<ControlTemplate " + Ns + " TargetType='Button'><t:TemplatePriorityProbe" + attribute + ">" + content + "</t:TemplatePriorityProbe></ControlTemplate>"
            : "<t:TemplatePriorityProbe " + Ns + attribute + ">" + content + "</t:TemplatePriorityProbe>";
        foreach (var root in CompileBoth<object>(xaml))
        {
            var control = Assert.IsType<TemplatePriorityProbe>(root is IControlTemplate template ? template.Build(new Button())!.Result : root);
            Assert.Equal(42d, control.Value);
            Assert.Equal(inTemplate ? 0 : 1, control.Writes);
        }
    }

    [AvaloniaTheory]
    [InlineData("Value='{t:TypedUnset}'", "")]
    [InlineData("", "<Setter.Value><t:TypedUnsetExtension/></Setter.Value>")]
    public void StyleSettersPreserveAndEvaluateTypedUnsetValues(string attribute, string content)
    {
        var xaml = "<Style " + Ns + " Selector='t|RegisteredSpecialValueProbe'><Setter Property='Value' " + attribute + ">" + content + "</Setter></Style>";
        foreach (var style in CompileBoth<Style>(xaml, Reset))
        {
            Assert.Same(AvaloniaProperty.UnsetValue, Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
            Assert.Equal(1, UnsetProviderCalls.Constructions);
            Assert.Equal(1, UnsetProviderCalls.Provides);
        }
    }

    [AvaloniaTheory]
    [InlineData("Value='{t:RegisteredString}'", "")]
    [InlineData("", "<Setter.Value><t:RegisteredStringExtension/></Setter.Value>")]
    public void StyleSettersRetainProvidedValuesWithoutPropertyConversion(string attribute, string content)
    {
        var xaml = "<Style " + Ns + " Selector='t|RegisteredSpecialValueProbe'><Setter Property='Value' " + attribute + ">" + content + "</Setter></Style>";
        foreach (var style in CompileBoth<Style>(xaml))
            Assert.Equal("42", Assert.IsType<Setter>(Assert.Single(style.Setters)).Value);
    }

    [AvaloniaTheory]
    [InlineData("Text='{t:BoxedNameBinding}'", "")]
    [InlineData("", "<TextBlock.Text><t:BoxedNameBindingExtension/></TextBlock.Text>")]
    public void BoxedBindingProvidersSubscribeInAttributeAndObjectForms(string attribute, string property)
    {
        var xaml = "<TextBlock " + Ns + " " + attribute + ">" + property + "</TextBlock>";
        foreach (var text in CompileBoth<TextBlock>(xaml))
        {
            var model = new BindingFixtureModel { Name = "provided binding" };
            text.DataContext = model;
            Assert.Equal(model.Name, text.Text);
            model.Name = "changed";
            Assert.Equal(model.Name, text.Text);
        }
    }

    [AvaloniaFact]
    public void BoxedBindingProvidersRespectAssignBinding()
    {
        var xaml = "<ItemsControl " + Ns + " DisplayMemberBinding='{t:BoxedNameBinding}'/>";
        foreach (var control in CompileBoth<ItemsControl>(xaml))
            Assert.Equal(nameof(BindingFixtureModel.Name), Assert.IsType<ReflectionBinding>(control.DisplayMemberBinding).Path);
    }

    private static void AssertCleared(RegisteredSpecialValueProbe control)
    {
        Assert.Equal(17d, control.Value);
        Assert.Equal(-7, control.DirectValue);
        Assert.Equal("style", control.Tag);
        Assert.Equal(9, Grid.GetRow(control));
        Assert.Equal("style", Assert.IsType<ReflectionBinding>(control.Assigned).Path);
    }

    private static void Reset() => UnsetProviderCalls.Constructions = UnsetProviderCalls.Provides = 0;
    private static IEnumerable<T> CompileBoth<T>(string xaml, Action? beforeCompile = null)
    {
        beforeCompile?.Invoke();
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        yield return Assert.IsAssignableFrom<T>(baseline.Root);
        beforeCompile?.Invoke();
        yield return Assert.IsAssignableFrom<T>(new ResourceProjectFixture(new[] { ("Special.axaml", xaml) }).Build("Special.axaml"));
    }
}

public sealed class RegisteredSpecialValueProbe : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<RegisteredSpecialValueProbe, double>(nameof(Value));
    public static readonly StyledProperty<BindingBase?> AssignedProperty = AvaloniaProperty.Register<RegisteredSpecialValueProbe, BindingBase?>(nameof(Assigned));
    public static readonly DirectProperty<RegisteredSpecialValueProbe, int> DirectValueProperty = AvaloniaProperty.RegisterDirect<RegisteredSpecialValueProbe, int>(nameof(DirectValue), target => target.DirectValue, (target, value) => target.DirectValue = value, unsetValue: -7);
    private int _directValue = 23;
    public RegisteredSpecialValueProbe()
    {
        SetValue(ValueProperty, 17d, BindingPriority.Style); SetValue(ValueProperty, 41d);
        SetValue(TagProperty, "style", BindingPriority.Style); SetValue(TagProperty, "local");
        SetValue(Grid.RowProperty, 9, BindingPriority.Style); SetValue(Grid.RowProperty, 4);
        SetValue(AssignedProperty, new ReflectionBinding("style"), BindingPriority.Style);
        SetValue(AssignedProperty, new ReflectionBinding("local"));
    }
    public int Writes { get; private set; }
    public int DirectWrites { get; private set; }
    public double Value { get => GetValue(ValueProperty); set { Writes++; SetValue(ValueProperty, value); } }
    public int DirectValue { get => _directValue; set { DirectWrites++; SetAndRaise(DirectValueProperty, ref _directValue, value); } }
    [AssignBinding] public BindingBase? Assigned { get => GetValue(AssignedProperty); set => SetValue(AssignedProperty, value); }
}

public static class UnsetProviderCalls
{
    public static int Constructions { get; set; }
    public static int Provides { get; set; }
}
public sealed class TypedUnsetExtension
{
    public TypedUnsetExtension() => UnsetProviderCalls.Constructions++;
    public UnsetValueType ProvideValue() { UnsetProviderCalls.Provides++; return (UnsetValueType)AvaloniaProperty.UnsetValue; }
}
public sealed class BoxedUnsetExtension
{
    public BoxedUnsetExtension() => UnsetProviderCalls.Constructions++;
    public object ProvideValue() { UnsetProviderCalls.Provides++; return AvaloniaProperty.UnsetValue; }
}
public sealed class RegisteredStringExtension { public string ProvideValue() => "42"; }
public sealed class BoxedNameBindingExtension { public object ProvideValue() => new ReflectionBinding(nameof(BindingFixtureModel.Name)); }
