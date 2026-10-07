using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Avalonia.Styling;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SetterContractTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests' xmlns:av='https://github.com/avaloniaui'";

    [AvaloniaTheory]
    [InlineData("<Setter/>")]
    [InlineData("<Setter Property='Control.Width' Value='42'/>")]
    [InlineData("<t:SetterHost><Setter Property='Control.Width' Value='42'/></t:SetterHost>")]
    [InlineData("<ControlTemplate TargetType='Button'><ContentControl><Setter Property='Control.Width' Value='42'/></ContentControl></ControlTemplate>")]
    [InlineData("<Style><Setter Property='Control.Width' Value='42'/></Style>")]
    [InlineData("<Style Selector='.accent'><Setter Property='Control.Width' Value='42'/></Style>")]
    [InlineData("<Style Selector='Button'><Style Selector='.accent'><Setter Property='Control.Width' Value='42'/></Style></Style>")]
    [InlineData("<Style Selector='Button'><Setter/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Value='42'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter><Setter.Property/></Setter></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='{x:Static Control.WidthProperty}' Value='42'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter><Setter.Property><x:Static Member='Control.WidthProperty'/></Setter.Property></Setter></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='{x:Null}'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter><Setter.Property><x:String>Width</x:String></Setter.Property></Setter></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='{t:SetterPropertyValue}'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter><Setter.Property><t:SetterPropertyValue/></Setter.Property></Setter></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property=''/></Style>")]
    [InlineData("<Style Selector='t|SelectorPropertyControl'><Setter Property='Unwrapped' Value='7'/></Style>")]
    [InlineData("<Style Selector='t|SetterInheritedPropertyControl'><Setter Property='AttachedValue' Value='42'/></Style>")]
    public void InvalidScopesAndPropertyFormsFailDuringBinding(string content)
    {
        var xaml = Document(content);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var fixture = new ResourceProjectFixture(new[] { ("Setters.axaml", xaml) });
        Assert.False(fixture.Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("<Setter x:SetterTargetType='Button' Property='IsDefault' Value='True'/>")]
    [InlineData("<t:SetterHost x:SetterTargetType='Button'><Setter Property='IsDefault' Value='True'/></t:SetterHost>")]
    [InlineData("<Style Selector='Border'><Setter x:SetterTargetType='Button' Property='IsDefault' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='Width'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Setter.Property='Width' Value='42'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Value='42'><Setter.Property>Width</Setter.Property></Setter></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='Control.Width' Value='42'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='Grid.Row' Value='2'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='(Grid.Row)' Value='2'/></Style>")]
    [InlineData("<Style Selector='Grid'><Setter Property='Row' Value='2'/></Style>")]
    [InlineData("<Style Selector='t|SelectorPropertyControl'><Setter Property='Different' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SelectorPropertyControl'><Setter Property='t:SelectorPropertyControl.Different' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SelectorPropertyControl'><Setter Property='t:SelectorPropertyControl.Unwrapped' Value='7'/></Style>")]
    [InlineData("<Style Selector='t|PathRegistrationDerived'><Setter Property='Wrapped' Value='base'/></Style>")]
    [InlineData("<Style Selector='t|PathRegistrationDerived'><Setter Property='t:PathRegistrationDerived.Wrapped' Value='7'/></Style>")]
    [InlineData("<Style Selector='t|SelectorPropertyControl'><Setter Property='Token' Value='enabled'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='Untyped' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='Token' Value='enabled'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='t:SetterUntypedPropertyControl.Token' Value='enabled'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='AttachedValue' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='t:SetterUntypedPropertyControl.AttachedValue' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='WriteOnly' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='ReadOnly' Value='42'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='Hook'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='AttachedHook'/></Style>")]
    [InlineData("<Style Selector='t|SetterInheritedPropertyControl'><Setter Property='t:SetterInheritedPropertyControl.AttachedValue' Value='42'/></Style>")]
    public void PropertyResolutionRetainsThePinnedDescriptorAndValueType(string content)
    {
        var xaml = Document(content);
        var expected = Setter(Baseline(xaml));
        var actual = Setter(Build(xaml));
        Assert.Same(expected.Property, actual.Property);
        Assert.Equal(expected.Value?.GetType(), actual.Value?.GetType());
        Assert.Equal(expected.Value, actual.Value);
    }

    [AvaloniaTheory]
    [InlineData("Button.IsDefault", true)]
    [InlineData("(Button.IsDefault)", true)]
    [InlineData("av:Button.IsDefault", true)]
    [InlineData("(av:Button.IsDefault)", true)]
    [InlineData("Button.av:IsDefault", true)]
    [InlineData("(Button.av:IsDefault)", true)]
    [InlineData("Classes._accent", true)]
    [InlineData("Classes.ąccent", true)]
    [InlineData("Classes.a\u0301", true)]
    [InlineData("Classes.accent_2", true)]
    [InlineData(" IsDefault", false)]
    [InlineData("IsDefault ", false)]
    [InlineData("(IsDefault)", false)]
    [InlineData("( Button.IsDefault)", false)]
    [InlineData("(Button.IsDefault )", false)]
    [InlineData("Button. IsDefault", false)]
    [InlineData("av|Button.IsDefault", false)]
    [InlineData("av:IsDefault", false)]
    [InlineData("Button.IsDefault.extra", false)]
    [InlineData("(Button.IsDefault))", false)]
    [InlineData("(Button.IsDefault", false)]
    [InlineData("Classes.2accent", false)]
    [InlineData("Classes.accent-primary", false)]
    [InlineData("Classes.accent primary", false)]
    [InlineData("Classes.:accent", false)]
    public void PropertyLiteralsFollowThePinnedIdentifierGrammar(string property, bool valid)
    {
        var xaml = Document("<Style Selector='Button'><Setter Property='" + property + "' Value='True'/></Style>");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Equal(valid, baseline.Error == null);
        var fixture = new ResourceProjectFixture(new[] { ("Setters.axaml", xaml) });
        Assert.Equal(valid, fixture.Result.Success);
        if (valid)
        {
            var expected = Setter(baseline.Root!);
            var actual = Setter(fixture.Build("Setters.axaml"));
            Assert.Same(expected.Property, actual.Property);
            Assert.Equal(expected.Value, actual.Value);
        }
    }

    [AvaloniaTheory]
    [InlineData("<Setter.Property> Width </Setter.Property>", false)]
    [InlineData("<Setter.Property>\n  Width\n</Setter.Property>", false)]
    [InlineData("<Setter.Property xml:space='preserve'> Width </Setter.Property>", false)]
    [InlineData("<Setter.Property>Width<x:Null/></Setter.Property>", true)]
    [InlineData("<Setter.Property><x:Null/>Width</Setter.Property>", true)]
    [InlineData("<Setter.Property>\n<x:Null/>Width</Setter.Property>", false)]
    public void PropertyElementsRetainTheFirstLiteralText(string property, bool valid)
    {
        var xaml = Document("<Style Selector='Button'><Setter Value='42'>" + property + "</Setter></Style>");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Equal(valid, baseline.Error == null);
        var fixture = new ResourceProjectFixture(new[] { ("Setters.axaml", xaml) });
        Assert.Equal(valid, fixture.Result.Success);
        if (valid)
        {
            var expected = Setter(baseline.Root!);
            var actual = Setter(fixture.Build("Setters.axaml"));
            Assert.Same(expected.Property, actual.Property);
            Assert.Equal(expected.Value, actual.Value);
        }
    }

    [AvaloniaFact]
    public void NativePropertyElementsRetainTheirNamespaceScope()
    {
        var xaml = Document("<Style Selector='Button'><Setter Value='42'><Setter.Property xmlns:c='https://github.com/avaloniaui'>c:Control.Width</Setter.Property></Setter></Style>");
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        var setter = Setter(Build(xaml));
        Assert.Same(Control.WidthProperty, setter.Property);
        Assert.Equal(42d, setter.Value);
    }

    [AvaloniaFact]
    public void ATemplateDoesNotReplaceTheEnclosingStyleTargetForSetters()
    {
        var xaml = Document("<Style Selector='Button'><Style.Resources><ControlTemplate x:Key='template' TargetType='Border'><ContentControl><Setter Property='IsDefault' Value='True'/></ContentControl></ControlTemplate></Style.Resources></Style>");
        foreach (var root in new[] { Baseline(xaml), Build(xaml) })
        {
            var style = Assert.IsType<Style>(root);
            var template = Assert.IsAssignableFrom<global::Avalonia.Controls.Templates.IControlTemplate>(style.Resources["template"]);
            var control = Assert.IsType<ContentControl>(template.Build(new Button())!.Result);
            var setter = Assert.IsType<Setter>(control.Content);
            Assert.Same(Button.IsDefaultProperty, setter.Property);
            Assert.Equal(true, setter.Value);
        }
    }

    [AvaloniaTheory]
    [InlineData("<Setter x:SetterTargetType='Button' Property='Tag' Value='{CompiledBinding $self.IsDefault}'/>")]
    [InlineData("<t:SetterHost x:SetterTargetType='Button'><Setter Property='Tag' Value='{CompiledBinding $self.IsDefault}'/></t:SetterHost>")]
    [InlineData("<Style Selector='Border'><Setter x:SetterTargetType='Button' Property='Tag' Value='{CompiledBinding $self.IsDefault}'/></Style>")]
    public void SelfBindingsUseTheExplicitSetterTarget(string content)
    {
        var xaml = Document(content);
        foreach (var root in new[] { Baseline(xaml), Build(xaml) })
        {
            var button = new Button { IsDefault = true };
            button.Styles.Add(new Style(selector => selector.OfType<Button>()) { Setters = { Setter(root) } });
            var window = new Window { Content = button };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.Equal(true, button.Tag);
                button.IsDefault = false;
                Assert.Equal(false, button.Tag);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData("Button", "Classes.accent")]
    [InlineData(":is(Button)", "(Classes.accent)")]
    [InlineData("Button", "classes.accent")]
    [InlineData("Button", "(cLaSsEs.accent)")]
    [InlineData("Button.foo:is(Button)", "Classes.accent")]
    [InlineData("StackPanel > Button", "Classes.accent")]
    public void ClassSettersUseThePublicClassPropertyContract(string selector, string property)
    {
        var xaml = Document("<Style Selector='" + selector + "'><Setter Property='" + property + "' Value='True'/></Style>");
        var expected = Setter(Baseline(xaml));
        var actual = Setter(Build(xaml));
        Assert.Same(expected.Property, actual.Property);
        Assert.Equal(typeof(bool), actual.Property!.PropertyType);
        Assert.Equal(true, actual.Value);
    }

    [AvaloniaTheory]
    [InlineData("<Style Selector='Button.accent'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button:pointerover'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button#named'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button[IsDefault=True]'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button,TextBox'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button:not(.disabled)'><Setter Property='Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button:pointerover' x:SetterTargetType='Button'><Setter Property='Classes.accent' Value='True'/></Style>")]
    [InlineData("<ControlTheme TargetType='Button'><Style Selector='^'><Setter Property='Classes.active' Value='True'/></Style></ControlTheme>")]
    [InlineData("<Button><Button.Styles><Style Selector=''><Setter Property='Classes.active' Value='True'/></Style></Button.Styles></Button>")]
    [InlineData("<Style Selector='Button'><Setter Property='av:Classes.active' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='Classes.' Value='True'/></Style>")]
    [InlineData("<Style Selector='Button'><Setter Property='Classes.accent' Value='{CompiledBinding $self.(Classes.accent)}'/></Style>")]
    [InlineData("<Style Selector='t|SetterUntypedPropertyControl'><Setter Property='t:SetterUntypedPropertyControl.Untyped' Value='42'/></Style>")]
    public void UnsupportedClassSettersFailBinding(string content)
    {
        var xaml = Document(content);
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Setters.axaml", xaml) }).Result.Success);
    }

    [AvaloniaTheory]
    [InlineData("<ControlTheme TargetType='Button'><Setter Property='Classes.accent' Value='True'/></ControlTheme>")]
    [InlineData("<Button><Button.Styles><Style><Setter Property='Classes.accent' Value='True'/></Style></Button.Styles></Button>")]
    [InlineData("<Style Selector='Button:pointerover'><Setter x:SetterTargetType='Button' Property='Classes.accent' Value='True'/></Style>")]
    public void ClassSetterValidationUsesTheNearestStyleMetadata(string content)
    {
        var xaml = Document(content);
        object? descriptor = null;
        foreach (var root in new[] { Baseline(xaml), Build(xaml) })
        {
            var setter = root is Button button ? Setter(button.Styles.Single()) : Setter(root);
            descriptor ??= setter.Property;
            Assert.Same(descriptor, setter.Property);
            Assert.Equal(true, setter.Value);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassSettersApplyAndBindingsUpdateStyledControls(bool binding)
    {
        var xaml = Document("<StackPanel><StackPanel.Styles><Style Selector='Button' x:DataType='t:BindingFixtureModel'><Setter Property='Classes.accent' Value='" + (binding ? "{Binding Enabled}" : "True") + "'/></Style></StackPanel.Styles><Button/><Button/></StackPanel>");
        foreach (var root in new[] { Baseline(xaml), Build(xaml) })
        {
            var panel = Assert.IsType<StackPanel>(root);
            var model = new BindingFixtureModel();
            panel.DataContext = model;
            var window = new Window { Content = panel };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.All(panel.Children, control => Assert.Contains("accent", control.Classes));
                model.Enabled = false;
                Assert.All(panel.Children, control => Assert.Equal(!binding, control.Classes.Contains("accent")));
            }
            finally { window.Close(); }
        }
    }

    private static string Document(string content) => content.Insert(content.IndexOfAny(new[] { ' ', '/', '>' }, 1), " " + Ns);
    private static object Baseline(string xaml)
    {
        var compiled = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.Null(compiled.Error);
        return compiled.Root!;
    }
    private static object Build(string xaml) => new ResourceProjectFixture(new[] { ("Setters.axaml", xaml) }).Build("Setters.axaml");
    private static Setter Setter(object root) => root switch
    {
        Setter setter => setter,
        StyleBase style => Assert.IsType<Setter>(Assert.Single(style.Setters)),
        SetterHost host => Assert.IsType<Setter>(host.Content),
        _ => throw new InvalidOperationException("Unexpected setter owner.")
    };
}

public sealed class SetterHost
{
    [Content] public object? Content { get; set; }
}

public sealed class SetterPropertyValue
{
    public AvaloniaProperty ProvideValue() => Control.WidthProperty;
}

public class SetterUntypedPropertyControl : Control
{
    public static readonly AvaloniaProperty UntypedProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, int>(nameof(Untyped));
    [TypeConverter(typeof(SelectorRejectedMemberConverter))]
    public static readonly StyledProperty<SelectorToken> TokenProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, SelectorToken>(nameof(Token));
    public static readonly StyledProperty<object> AttachedValueProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, object>("AttachedValue", 0);
    public static readonly StyledProperty<object> WriteOnlyProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, object>("WriteOnly", 0);
    public static readonly StyledProperty<object> ReadOnlyProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, object>("ReadOnly", 0);
    public static readonly StyledProperty<EventHandler?> HookProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, EventHandler?>(nameof(Hook));
    public static readonly StyledProperty<EventHandler?> AttachedHookProperty = AvaloniaProperty.Register<SetterUntypedPropertyControl, EventHandler?>("AttachedHook");
    public int Untyped { get => (int)GetValue(UntypedProperty)!; set => SetValue(UntypedProperty, value); }
    public SelectorToken Token { get => GetValue(TokenProperty); set => SetValue(TokenProperty, value); }
    public static int GetAttachedValue(Control target) => (int)target.GetValue(AttachedValueProperty);
    public static void SetAttachedValue(Control target, string value) => target.SetValue(AttachedValueProperty, value);
    public static void SetWriteOnly(Control target, int value) => target.SetValue(WriteOnlyProperty, value);
    public static int GetReadOnly(Control target) => (int)target.GetValue(ReadOnlyProperty);
    public event EventHandler Hook { add { } remove { } }
    public static void AddAttachedHookHandler(Control target, EventHandler handler) { }
}

public sealed class SetterInheritedPropertyControl : SetterUntypedPropertyControl { }
