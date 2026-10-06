using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.GestureRecognizers;
using Microsoft.CodeAnalysis;
using XamlG.AvaloniaRuntime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RegisteredMetadataRegressionTests
{
    [Fact]
    public void PublicMetadataImportDoesNotRequireThePrivateClrSetter()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<ScrollGestureRecognizer " +
            ResourceProjectFixture.Namespace + " Offset='12,34'/>") });
        Assert.Equal(MetadataImportOptions.Public, fixture.Compilation.Options.MetadataImportOptions);
        var type = fixture.Compilation.GetTypeByMetadataName(typeof(ScrollGestureRecognizer).FullName!)!;
        var property = Assert.Single(type.GetMembers(nameof(ScrollGestureRecognizer.Offset)).OfType<IPropertySymbol>());
        var registration = Assert.Single(type.GetMembers(nameof(ScrollGestureRecognizer.OffsetProperty)).OfType<IFieldSymbol>());
        Assert.Null(property.SetMethod);
        Assert.True(registration.IsStatic);
        Assert.Equal(Accessibility.Public, registration.DeclaredAccessibility);
        Assert.True(fixture.Result.Success, string.Join("\n", fixture.Result.Documents.SelectMany(d => d.Output.Diagnostics)));
    }

    [AvaloniaFact]
    public void StaticPreferencesPreserveOrderAndDuplicates()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Window " + ResourceProjectFixture.Namespace +
            " TransparencyLevelHint='Mica,Blur,Transparent,Blur'/>") });
        var window = Assert.IsType<Window>(fixture.Build("View.axaml"));
        Assert.Equal(new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur }, window.TransparencyLevelHint);
    }

    [Fact]
    public void UnknownPreferenceRejectsTheWholeDocument()
    {
        var fixture = new ResourceProjectFixture(new[] { ("View.axaml", "<Window " + ResourceProjectFixture.Namespace +
            " TransparencyLevelHint='Transparent,UnknownPreference,Blur'/>") });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.SelectMany(d => d.Output.Diagnostics), d => d.Code == "XG1008");
    }

    [AvaloniaFact]
    public void AdapterDoesNotBypassReadOnlyRegistration()
    {
        var target = new ReadOnlyRegistrationProbe();
        Assert.True(ReadOnlyRegistrationProbe.ValueProperty.IsReadOnly);
        Assert.Throws<ArgumentException>(() => AvaloniaRegisteredSetter.Assign(target, ReadOnlyRegistrationProbe.ValueProperty, 99));
        Assert.Equal(17, target.Value);
    }
}
