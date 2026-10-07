using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RemainingTransformTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("<DoubleTransition Property='Opacity' Duration='0:0:1'/>")]
    [InlineData("<DoubleTransition Property='Control.Opacity' Duration='0:0:1'/>")]
    [InlineData("<Transitions><DoubleTransition Property='Opacity' Duration='0:0:1'/></Transitions>")]
    [InlineData("<Border><Border.Transitions><Transitions><DoubleTransition Property='Opacity' Duration='0:0:1'/></Transitions></Border.Transitions></Border>")]
    [InlineData("<Border><Border.Transitions><DoubleTransition Property='Opacity' Duration='0:0:1'/></Border.Transitions></Border>")]
    [InlineData("<Border><Border.Tag><DoubleTransition Property='Opacity' Duration='0:0:1'/></Border.Tag></Border>")]
    [InlineData("<Border><Border.Tag><DoubleTransition Property='Control.Opacity' Duration='0:0:1'/></Border.Tag></Border>")]
    [InlineData("<Style Selector='Border'><Setter Property='Transitions'><Transitions><DoubleTransition Property='Opacity' Duration='0:0:1'/></Transitions></Setter></Style>")]
    [InlineData("<Style Selector='Border'><Style.Animations><Animation Duration='0:0:1'><KeyFrame Cue='0%'><Setter Property='Opacity' Value='0.25'/></KeyFrame><KeyFrame Cue='100%'><Setter Property='Opacity' Value='1'/></KeyFrame></Animation></Style.Animations></Style>")]
    [InlineData("<Animation x:SetterTargetType='Border'><KeyFrame Cue='0%'><Setter Property='Opacity' Value='0.25'/></KeyFrame></Animation>")]
    [InlineData("<Animation><KeyFrame Cue='0%'><Setter Property='Opacity' Value='0.25'/></KeyFrame></Animation>")]
    [InlineData("<ControlTemplate TargetType='Border'><Border.Tag><DoubleTransition Property='Opacity'/></Border.Tag></ControlTemplate>")]
    public void AnimationAndTransitionPropertyScopesMatchThePinnedTransforms(string body)
    {
        var end = body.IndexOf('>'); if (body[end - 1] == '/') end--;
        var xaml = body.Insert(end, " " + Ns);
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        var fixture = new ResourceProjectFixture(new[] { ("Transform.axaml", xaml) });
        if (baseline.Error?.GetBaseException() is NullReferenceException)
        {
            Assert.True(fixture.Result.Success);
            Assert.IsType<NullReferenceException>(Record.Exception(() => fixture.Build("Transform.axaml"))?.GetBaseException());
            return;
        }
        Assert.True((baseline.Error == null) == fixture.Result.Success, "Baseline: " + baseline.Error + "\nNative: " + string.Join("\n", fixture.Result.Documents.SelectMany(document => document.Output.Diagnostics)));
        if (baseline.Error == null) fixture.Build("Transform.axaml");
    }

    [AvaloniaTheory]
    [InlineData("{OnPlatform 1, Default=2}", 2d)]
    [InlineData("{t:OrderedOption Value=17}", 17d)]
    public void OptionDefaultsAndPredicateOverloadsFollowSourceOrder(string markup, double expected)
    {
        var xaml = "<Border " + Ns + " Width='" + markup + "'/>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml); Assert.Null(baseline.Error);
        foreach (var root in new[] { Assert.IsType<Border>(baseline.Root), Assert.IsType<Border>(AvaloniaCompilation.Build(xaml)) }) Assert.Equal(expected, root.Width);
    }

    [AvaloniaTheory]
    [InlineData("Default", "2")]
    [InlineData("Selected", "1")]
    public void RepeatedOptionElementsKeepThePinnedSelectionOrder(string property, string expected)
    {
        var xaml = "<Border " + Ns + "><Border.Tag><t:OptionProbe><t:OptionProbe." + property + ">1</t:OptionProbe." + property + "><t:OptionProbe." + property + ">2</t:OptionProbe." + property + "></t:OptionProbe></Border.Tag></Border>";
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml); Assert.Null(baseline.Error);
        foreach (var root in new[] { Assert.IsType<Border>(baseline.Root), Assert.IsType<Border>(AvaloniaCompilation.Build(xaml)) }) Assert.Equal(expected, root.Tag);
    }
}

public sealed class OrderedOptionExtension
{
    [MarkupExtensionOption(1)] public object? Value { get; set; }
    public bool ShouldProvideOption(string value) => value == "1";
    public bool ShouldProvideOption(int value) => false;
    public object ProvideValue() => throw new InvalidOperationException("Compiler-only option.");
}
