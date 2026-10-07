using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class TemplatePriorityTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("ControlTemplate", "42")]
    [InlineData("ControlTemplate", "{t:TypedTemplateNumber}")]
    [InlineData("ControlTemplate", "{t:BoxedTemplateNumber}")]
    [InlineData("t:DerivedControlTemplate", "42")]
    [InlineData("t:InterfaceTemplate", "42")]
    public void TemplateValuesBypassTheClrWrapperAndAllowStyleTriggers(string templateType, string value)
    {
        var xaml = "<" + templateType + " " + Ns + " TargetType='Button'><t:TemplatePriorityProbe Value='" + value + "' DirectValue='7' Grid.Row='2'/></" + templateType + ">";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var actual = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml");
        foreach (var template in new[] { Assert.IsAssignableFrom<IControlTemplate>(baseline.Root), Assert.IsAssignableFrom<IControlTemplate>(actual) })
        {
            var control = Assert.IsType<TemplatePriorityProbe>(template.Build(new Button())!.Result);
            Assert.Equal(42d, control.Value);
            Assert.Equal(0, control.Writes);
            Assert.Equal(7, control.DirectValue);
            Assert.Equal(1, control.DirectWrites);
            Assert.Equal(BindingPriority.Template, control.GetDiagnostic(TemplatePriorityProbe.ValueProperty).Priority);
            Assert.Equal(2, Grid.GetRow(control));
            Assert.Equal(BindingPriority.Template, control.GetDiagnostic(Grid.RowProperty).Priority);
            using (control.SetValue(TemplatePriorityProbe.ValueProperty, 99d, BindingPriority.StyleTrigger)) Assert.Equal(99d, control.Value);
            Assert.Equal(42d, control.Value);
        }
    }

    [AvaloniaTheory]
    [InlineData("42", 42d, BindingPriority.Template)]
    [InlineData("{StaticResource W}", 42d, BindingPriority.Template)]
    [InlineData("{DynamicResource W}", 42d, BindingPriority.Template)]
    [InlineData("{TemplateBinding Width}", 100d, BindingPriority.Template)]
    [InlineData("{ReflectionBinding Width, RelativeSource={RelativeSource TemplatedParent}}", 100d, BindingPriority.LocalValue)]
    [InlineData("{CompiledBinding Width, RelativeSource={RelativeSource TemplatedParent}}", 100d, BindingPriority.LocalValue)]
    public void LiteralResourceAndBindingPrioritiesMatchTheUpstreamCompiler(string value, double expectedValue, BindingPriority priority)
    {
        var xaml = "<Button " + Ns + " Width='100'><Button.Resources><x:Double x:Key='W'>42</x:Double></Button.Resources>" +
            "<Button.Template><ControlTemplate><Border Name='part' Width='" + value + "'/></ControlTemplate></Button.Template></Button>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var actual = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml");
        foreach (var root in new[] { Assert.IsType<Button>(baseline.Root), Assert.IsType<Button>(actual) })
        {
            var window = new Window { Content = root };
            try
            {
                window.Show(); window.UpdateLayout();
                var part = Assert.Single(root.GetVisualDescendants().OfType<Border>(), child => child.Name == "part");
                Assert.Equal(expectedValue, part.Width);
                Assert.Equal(priority, part.GetDiagnostic(Control.WidthProperty).Priority);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public void ValuesOutsideTemplatesRetainTheClrSetterAndLocalPriority()
    {
        var xaml = "<t:TemplatePriorityProbe " + Ns + " Value='42'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var actual = new ResourceProjectFixture(new[] { ("Control.axaml", xaml) }).Build("Control.axaml");
        foreach (var control in new[] { Assert.IsType<TemplatePriorityProbe>(baseline.Root), Assert.IsType<TemplatePriorityProbe>(actual) })
        {
            Assert.Equal(42d, control.Value);
            Assert.Equal(1, control.Writes);
            Assert.Equal(BindingPriority.LocalValue, control.GetDiagnostic(TemplatePriorityProbe.ValueProperty).Priority);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, BindingPriority.LocalValue)]
    [InlineData(true, BindingPriority.Template)]
    public void ObjectAndPropertyElementValuesRetainTheirUpstreamPriorities(bool explicitContent, BindingPriority contentPriority)
    {
        var content = "<t:TemplatePriorityProbe><t:TemplatePriorityProbe.Value><x:Double>42</x:Double></t:TemplatePriorityProbe.Value></t:TemplatePriorityProbe>";
        if (explicitContent) content = "<ContentControl.Content>" + content + "</ContentControl.Content>";
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><ContentControl>" + content + "</ContentControl></ControlTemplate>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var actual = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml");
        foreach (var template in new[] { Assert.IsAssignableFrom<IControlTemplate>(baseline.Root), Assert.IsAssignableFrom<IControlTemplate>(actual) })
        {
            var root = Assert.IsType<ContentControl>(template.Build(new Button())!.Result);
            var child = Assert.IsType<TemplatePriorityProbe>(root.Content);
            Assert.Equal(42d, child.Value);
            Assert.Equal(0, child.Writes);
            Assert.Equal(contentPriority, root.GetDiagnostic(ContentControl.ContentProperty).Priority);
            Assert.Equal(BindingPriority.Template, child.GetDiagnostic(TemplatePriorityProbe.ValueProperty).Priority);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignedBindingValuesRemainValuesInsteadOfSubscriptions(bool inTemplate)
    {
        var xaml = inTemplate
            ? "<ControlTemplate " + Ns + " TargetType='Button'><ItemsControl DisplayMemberBinding='{ReflectionBinding Name}'/></ControlTemplate>"
            : "<ItemsControl " + Ns + " DisplayMemberBinding='{ReflectionBinding Name}'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(baseline.Error);
        var actual = new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml");
        foreach (var root in new[] { baseline.Root, actual })
        {
            var control = Assert.IsType<ItemsControl>(root is IControlTemplate template ? template.Build(new Button())!.Result : root);
            Assert.Equal("Name", Assert.IsType<ReflectionBinding>(control.DisplayMemberBinding).Path);
            Assert.Equal(inTemplate ? BindingPriority.Template : BindingPriority.LocalValue, control.GetDiagnostic(ItemsControl.DisplayMemberBindingProperty).Priority);
        }
    }

    [AvaloniaFact]
    public void TemplateAssignmentsRetainSourceDeclarationsAndLiveSetters()
    {
        var xaml = "<ControlTemplate " + Ns + " TargetType='Button'><t:TemplatePriorityProbe Value='42'/></ControlTemplate>";
        var template = Assert.IsAssignableFrom<IControlTemplate>(new ResourceProjectFixture(new[] { ("Template.axaml", xaml) }).Build("Template.axaml"));
        var control = Assert.IsType<TemplatePriorityProbe>(template.Build(new Button())!.Result);
        Assert.True(XamlRuntimeSession.TryGet(control, out var session));
        var node = session!.FindNode(control);
        Assert.NotNull(node);
        Assert.NotNull(node.Source);
        Assert.Contains("Value", node.Source.Declarations.Keys);
        Assert.True(session.Apply(0, new[] { new XamlPropertyUpdate(node.Key, "Value", 77d) }).Applied);
        Assert.Equal(77d, control.Value);
        Assert.Equal(0, control.Writes);
        Assert.Equal(BindingPriority.Template, control.GetDiagnostic(TemplatePriorityProbe.ValueProperty).Priority);
    }
}

public sealed class TemplatePriorityProbe : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<TemplatePriorityProbe, double>(nameof(Value));
    public static readonly DirectProperty<TemplatePriorityProbe, int> DirectValueProperty = AvaloniaProperty.RegisterDirect<TemplatePriorityProbe, int>(nameof(DirectValue), target => target.DirectValue, (target, value) => target.DirectValue = value);
    private int _directValue;
    public int Writes { get; private set; }
    public int DirectWrites { get; private set; }
    public double Value { get => GetValue(ValueProperty); set { Writes++; SetValue(ValueProperty, value); } }
    public int DirectValue { get => _directValue; set { DirectWrites++; SetAndRaise(DirectValueProperty, ref _directValue, value); } }
}

public sealed class TypedTemplateNumberExtension { public double ProvideValue() => 42d; }
public sealed class BoxedTemplateNumberExtension { public object ProvideValue() => 42d; }
