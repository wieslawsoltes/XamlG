using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security;
using Avalonia;
using Avalonia.Controls;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

/// <summary>This inventory runs against both the packaged compatibility baseline and pinned source build.</summary>
public sealed class ParseTransformCoverageTests
{
    private static readonly IReadOnlyDictionary<string, string> Literals = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Avalonia.Point"] = "1,2", ["Avalonia.Vector"] = "1,2", ["Avalonia.Vector3D"] = "1,2,3",
        ["Avalonia.Size"] = "1,2", ["Avalonia.Rect"] = "1,2,3,4", ["Avalonia.Thickness"] = "1,2,3,4",
        ["Avalonia.CornerRadius"] = "1,2,3,4", ["Avalonia.Matrix"] = "1,0,0,1,2,3",
        ["Avalonia.RelativePoint"] = "10%,20%", ["Avalonia.RelativeScalar"] = "25%", ["Avalonia.RelativeRect"] = "0%,0%,50%,50%",
        ["Avalonia.PixelPoint"] = "1,2", ["Avalonia.PixelSize"] = "1,2", ["Avalonia.PixelRect"] = "1,2,3,4",
        ["Avalonia.Animation.Cue"] = "25%", ["Avalonia.Animation.IterationCount"] = "3", ["Avalonia.Animation.KeySpline"] = "0.1,0.2,0.3,0.4",
        ["Avalonia.Animation.Easings.Easing"] = "CubicEaseOut",
        ["Avalonia.Controls.Classes"] = "one,two", ["Avalonia.Controls.RowDefinitions"] = "Auto,2*",
        ["Avalonia.Controls.ColumnDefinitions"] = "Auto,2*", ["Avalonia.Controls.GridLength"] = "2*", ["Avalonia.Controls.FlexBasis"] = "25%",
        ["Avalonia.Input.Cursor"] = "Hand", ["Avalonia.Input.KeyGesture"] = "Ctrl+A",
        ["Avalonia.Media.BoxShadow"] = "1 2 3 red", ["Avalonia.Media.BoxShadows"] = "1 2 red,3 4 blue",
        ["Avalonia.Media.Brush"] = "Red", ["Avalonia.Media.SolidColorBrush"] = "Red", ["Avalonia.Media.Color"] = "#123456",
        ["Avalonia.Media.HslColor"] = "hsl(180,50%,50%)", ["Avalonia.Media.HsvColor"] = "hsv(180,50%,50%)",
        ["Avalonia.Media.Geometry"] = "M0,0L1,1", ["Avalonia.Media.StreamGeometry"] = "M0,0L1,1",
        ["Avalonia.Media.PathGeometry"] = "M0,0L1,1", ["Avalonia.Media.PathFigures"] = "M0,0L1,1",
        ["Avalonia.Media.Transform"] = "1,0,0,1,2,3", ["Avalonia.Media.Transformation.TransformOperations"] = "rotate(30deg)",
        // FontFamily keeps URI-context resolution in its public constructor.
        ["Avalonia.Media.FontFamily"] = "Arial", ["Avalonia.Media.FontFeature"] = "kern=1",
        ["Avalonia.Media.FontFeatureCollection"] = "kern=1,aalt[3:5]=2", ["Avalonia.Media.Fonts.OpenTypeTag"] = "wght",
        ["Avalonia.Media.FontVariationSettings"] = "wght=700,wdth=85",
        ["Avalonia.Media.TextTrimming"] = "WordEllipsis", ["Avalonia.Media.TextDecorationCollection"] = "Underline,Strikethrough",
        ["Avalonia.Media.UnicodeRange"] = "U+20-3F,30??", ["Avalonia.Media.UnicodeRangeSegment"] = "U+30??",
        ["Avalonia.Media.Effect"] = "blur(2)", ["Avalonia.Media.CacheMode"] = "BitmapCache"
    };

    [Fact]
    public void EveryPublicFrameworkStringParserHasAnExplicitTransformCase()
    {
        var types = new[] { typeof(Point).Assembly, typeof(Control).Assembly }.Distinct().SelectMany(assembly => assembly.GetExportedTypes());
        var unknown = types.Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Any(method => method.Name == "Parse" && method.GetParameters() is { Length: > 0 } parameters && parameters[0].ParameterType == typeof(string)))
            .Select(type => type.FullName!).Where(name => !Literals.ContainsKey(name)).OrderBy(name => name).ToArray();
        Assert.True(unknown.Length == 0, "Add parser lowering and behavioral coverage for: " + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryAvailableInventoryLiteralLowersWithoutRuntimeParseCalls()
    {
        var available = new[] { typeof(Point).Assembly, typeof(Control).Assembly }.Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes()).ToDictionary(type => type.FullName!, StringComparer.Ordinal);
        Assert.All(Literals.Keys.Where(name => !available.ContainsKey(name)), name =>
            Assert.Contains(name, new[] { "Avalonia.Controls.FlexBasis", "Avalonia.Media.FontVariationSettings" }));
        foreach (var entry in Literals)
        {
            if (!available.TryGetValue(entry.Key, out var type)) continue; // New source APIs are covered by the same test in ControlCatalog.Tests.
            var xaml = "<ResourceDictionary " + ResourceProjectFixture.Namespace + " xmlns:p='clr-namespace:" + type.Namespace +
                ";assembly=" + type.Assembly.GetName().Name + "'><p:" + type.Name + " x:Key='value'>" + SecurityElement.Escape(entry.Value) +
                "</p:" + type.Name + "></ResourceDictionary>";
            var fixture = new ResourceProjectFixture([("Inventory.axaml", xaml)]);
            Assert.True(fixture.Result.Success, entry.Key + ": " + string.Join("\n", fixture.Result.Documents.Single().Output.Diagnostics));
            var syntax = CSharpSyntaxTree.ParseText(fixture.Result.Documents.Single().Output.Source, cancellationToken: TestContext.Current.CancellationToken);
            var parseCalls = syntax.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.ValueText == "Parse").ToArray();
            Assert.True(parseCalls.Length == 0, entry.Key + " still emits runtime parsing: " + string.Join(", ", parseCalls.Select(call => call.ToString())));
            // Compile each generated program too, catching inaccessible members and
            // conversions that a successful binding result alone cannot validate.
            _ = fixture.Emit();
        }
    }
}
