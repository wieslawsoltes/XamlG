using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ConditionalClassTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("Classes.accent='False' Classes='accent primary'")]
    [InlineData("Classes='accent primary' Classes.accent='False'")]
    public void ConditionalClassesHaveOrderIndependentLiteralSemantics(string attributes)
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Ns + " " + attributes + "/>");
        Assert.DoesNotContain("accent", root.Classes);
        Assert.Contains("primary", root.Classes);
    }

    [AvaloniaFact]
    public void CompiledClassBindingsUpdateAndAreOwnedByTheView()
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Ns + " x:DataType='vm:BindingFixtureModel' Classes.accent='{Binding Enabled}'/>");
        var model = new BindingFixtureModel();
        root.DataContext = model;
        Assert.Contains("accent", root.Classes);
        model.Enabled = false;
        Assert.DoesNotContain("accent", root.Classes);
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        session!.Dispose();
        var before = root.Classes.Contains("accent");
        model.Enabled = true;
        Assert.Equal(before, root.Classes.Contains("accent"));
    }

    // Adapted from Avalonia.Markup.Xaml.UnitTests/SetterTests.cs at the pinned
    // upstream revision. Only the runtime-loading harness is replaced by XamlG.
    [AvaloniaTheory]
    [InlineData("{x:Type ContentControl}")]
    [InlineData("ContentControl")]
    public void AnimationSetterTargetTypeResolvesBothTypeForms(string targetType)
    {
        var animation = (global::Avalonia.Animation.Animation)AvaloniaCompilation.Build(
            "<Animation " + Ns + " x:SetterTargetType='" + targetType + "'><KeyFrame><Setter Property='Content' Value='{Binding}'/></KeyFrame></Animation>");
        var setter = Assert.IsType<Setter>(animation.Children[0].Setters[0]);
        Assert.NotNull(setter.Property);
        Assert.Equal(typeof(ContentControl), setter.Property.OwnerType);
    }
}
