using System.Text.Json;
using Microsoft.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using XamlG.IntelligentUI.Avalonia;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFullCSharpInteractionTests
{
    [AvaloniaFact]
    public void FullCSharpRecomputesAfterNativeSliderInputWithDecimalScale()
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        using var compiler = new UiCSharpExpressionCompiler(paths.Select(path => MetadataReference.CreateFromFile(path)), _ => true);
        var store = new UiSessionStore(new(expressionCompiler: compiler));
        var snapshot = store.Publish(UiCSharpExamples.Sum(), "owner");
        using var session = new UiAvaloniaSession(store, UiPresentation.From(snapshot), "owner");
        Assert.Null(session.Diagnostic);
        var slider = Assert.Single(session.View.GetLogicalDescendants().OfType<Slider>());
        slider.Value = 7;
        Assert.Null(session.Diagnostic);
        Assert.Equal(1, session.Snapshot!.StateRevision);
        Assert.Contains("Sum: 28", session.Snapshot.FallbackMarkdown);
        using var scaled = JsonDocument.Parse("7.0");
        var changed = store.ChangeState(new(snapshot.Id, snapshot.Revision, session.Snapshot.StateRevision, "n", scaled.RootElement), "owner");
        Assert.Contains("Sum: 28", changed.FallbackMarkdown);
    }
}
