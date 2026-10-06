using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class OptionMarkupTests
{
    private const string Local = " xmlns:p='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaFact]
    public void SelectedBranchIsLazyAndReceiverConstructedOnce()
    {
        OptionProbeExtension.Constructions = OptionProbeExtension.Predicates = ThrowingOptionExtension.Constructions = 0;
        var fixture = Fixture("<Button " + ResourceProjectFixture.Namespace + Local +
            " Tag='{p:OptionProbe Other={p:ThrowingOption}, Selected=chosen, Default={p:ThrowingOption}}'/>");
        Assert.Equal(0, OptionProbeExtension.Constructions);
        Assert.Equal(0, ThrowingOptionExtension.Constructions);
        var button = Assert.IsType<Button>(fixture.Build("View.axaml"));
        Assert.Equal("chosen", button.Tag);
        Assert.Equal(1, OptionProbeExtension.Constructions);
        Assert.Equal(2, OptionProbeExtension.Predicates);
        Assert.Equal(0, ThrowingOptionExtension.Constructions);
    }

    [AvaloniaFact]
    public void FormFactorValuesAreConvertedToTheEventualEnumType()
    {
        // The headless runtime exposes a Desktop form factor. The compiler must call
        // the framework predicate, then produce the enum rather than the extension.
        var flyout = Assert.IsType<Flyout>(Fixture("<Flyout " + ResourceProjectFixture.Namespace +
            " ShowMode='{OnFormFactor Standard, Desktop=Transient}'/>").Build("View.axaml"));
        Assert.Equal(FlyoutShowMode.Transient, flyout.ShowMode);
    }

    [AvaloniaFact]
    public void OptionsElementSupportsTypedValuesAndGroupedEntries()
    {
        var button = Assert.IsType<Button>(Fixture("<Button " + ResourceProjectFixture.Namespace +
            "><Button.Width><OnPlatform x:TypeArguments='x:Double' Default='9'><On Options='Windows, Linux, macOS'>17</On></OnPlatform></Button.Width></Button>").Build("View.axaml"));
        Assert.Equal(17, button.Width);
    }

    [AvaloniaFact]
    public void OptionPropertyElementRetainsTheAssignmentTargetType()
    {
        var flyout = Assert.IsType<Flyout>(Fixture("<Flyout " + ResourceProjectFixture.Namespace +
            "><Flyout.ShowMode><OnFormFactor><OnFormFactor.Default>Standard</OnFormFactor.Default><OnFormFactor.Mobile>Transient</OnFormFactor.Mobile></OnFormFactor></Flyout.ShowMode></Flyout>").Build("View.axaml"));
        Assert.Equal(FlyoutShowMode.Standard, flyout.ShowMode);
    }

    [Fact]
    public void UnknownAndDuplicateOptionsAreDiagnosed()
    {
        var unknown = Fixture("<Button " + ResourceProjectFixture.Namespace +
            "><Button.Width><OnPlatform><On Options='NotAPlatform'>17</On></OnPlatform></Button.Width></Button>");
        Assert.False(unknown.Result.Success);
        Assert.Contains(unknown.Result.Documents.SelectMany(d => d.Output.Diagnostics), d => d.Code == "XG1040");
        var duplicate = Fixture("<Button " + ResourceProjectFixture.Namespace +
            " Width='{OnPlatform 1, Default=2}'/>");
        Assert.False(duplicate.Result.Success);
        Assert.Contains(duplicate.Result.Documents.SelectMany(d => d.Output.Diagnostics), d => d.Code == "XG1040");
    }

    [AvaloniaFact]
    public void UnmatchedOptionUsesTypedDefaultWithoutEvaluatingItsValue()
    {
        ThrowingOptionExtension.Constructions = 0;
        var button = Assert.IsType<Button>(Fixture("<Button " + ResourceProjectFixture.Namespace + Local +
            " Tag='{p:OptionProbe Other={p:ThrowingOption}}'/>").Build("View.axaml"));
        Assert.Null(button.Tag);
        Assert.Equal(0, ThrowingOptionExtension.Constructions);
    }

    private static ResourceProjectFixture Fixture(string source) => new(new[] { ("View.axaml", source) });
}
