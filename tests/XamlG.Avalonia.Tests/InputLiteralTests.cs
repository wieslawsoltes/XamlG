using System.Globalization;
using System.Security;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using SourceInfo = Avalonia.Markup.Xaml.Diagnostics.XamlSourceInfo;

namespace XamlG.Avalonia.Tests;

public sealed class InputLiteralTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GesturesPreserveKeyAliasesModifiersAndTokenOrder(bool sourceInfo)
    {
        var literals = Enum.GetNames<Key>().Select(key => "Ctrl+" + key.ToLowerInvariant()).Concat(new[]
        {
            "Ctrl++", "Ctrl+-", "Ctrl+.", "Ctrl+,", "Ctrl+Shift+A", "cmd+A", "win+A", "⌘+A",
            "Alt+Meta+F12", " Control + shift + a ", "A+Ctrl", "Ctrl+A+Shift", "A+B", "Ctrl",
            "+", "-1", "+42", "2147483647", "-2147483648", "A, B", "Ctrl+Control+A", "Ctrl+++A"
        }).ToArray();
        var (fixture, actual, expected) = Compile("KeyGesture", literals, sourceInfo);
        for (var index = 0; index < literals.Length; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            var gesture = Assert.IsType<KeyGesture>(actual[key]);
            Assert.Equal(KeyGesture.Parse(literals[index]), gesture);
            Assert.Equal(expected[key], gesture);
            Assert.NotSame(expected[key], gesture);
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
            Assert.Null(SourceInfo.GetXamlSourceInfo(gesture));
        }
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CursorFallbackPreservesCaseInsensitiveGrammarAndSourceMetadata(bool sourceInfo)
    {
        var literals = Enum.GetNames<StandardCursorType>().SelectMany(cursor => new[] { cursor, cursor.ToLowerInvariant(), " " + cursor + " " })
            .Concat(new[] { "Hand,Arrow", "9", "+9", "4294967296" }).ToArray();
        var (fixture, actual, expected) = Compile("Cursor", literals, sourceInfo);
        for (var index = 0; index < literals.Length; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            using var cursor = Assert.IsType<Cursor>(actual[key]);
            using var baseline = Assert.IsType<Cursor>(expected[key]);
            Assert.Equal(baseline.ToString(), cursor.ToString());
            Assert.NotSame(baseline, cursor);
            Assert.Equal(SourceInfo.GetXamlSourceInfo(expected, key), SourceInfo.GetXamlSourceInfo(actual, key));
            Assert.Equal(SourceInfo.GetXamlSourceInfo(baseline), SourceInfo.GetXamlSourceInfo(cursor));
        }
        Assert.DoesNotMatch(@"\.\s*@?Parse\s*\(", fixture.Result.Documents.Single().Output.Source);
    }

    [AvaloniaTheory]
    [InlineData("KeyGesture", "Ctrl+")]
    [InlineData("KeyGesture", "Unknown+A")]
    [InlineData("KeyGesture", "2147483648")]
    [InlineData("KeyGesture", "Ctrl+NotAKey")]
    [InlineData("Cursor", "NotACursor")]
    [InlineData("Cursor", "Hand,Unknown")]
    [InlineData("Cursor", "9223372036854775808")]
    public void InvalidInputLiteralsReportDiagnostics(string type, string text)
    {
        var xaml = ResourceProjectFixture.Dictionary("<" + type + " x:Key='value'>" + SecurityElement.Escape(text) + "</" + type + ">");
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml);
        Assert.NotNull(baseline.Error ?? Record.Exception(() => _ = Assert.IsType<ResourceDictionary>(baseline.Root)["value"]));
        var fixture = new ResourceProjectFixture(new[] { ("Input.axaml", xaml) });
        Assert.False(fixture.Result.Success);
        Assert.Contains(fixture.Result.Documents.Single().Output.Diagnostics, diagnostic => diagnostic.Code == "XG3004");
    }

    private static (ResourceProjectFixture Fixture, ResourceDictionary Actual, ResourceDictionary Expected) Compile(string type, string[] literals, bool sourceInfo)
    {
        var xaml = ResourceProjectFixture.Dictionary(string.Join("\n", literals.Select((literal, index) =>
            "<" + type + " x:Key='" + index.ToString(CultureInfo.InvariantCulture) + "'>" + SecurityElement.Escape(literal) + "</" + type + ">")));
        var fixture = new ResourceProjectFixture(new[] { ("Input.axaml", xaml) }, createSourceInfo: sourceInfo);
        var actual = Assert.IsType<ResourceDictionary>(fixture.Build("Input.axaml"));
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml, sourceInfo, "Input.axaml");
        Assert.Null(baseline.Error);
        return (fixture, actual, Assert.IsType<ResourceDictionary>(baseline.Root));
    }
}
