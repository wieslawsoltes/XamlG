using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using XamlG.LanguageServer.Diagnostics;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class DiagnosticCacheBoundaryTests
{
    private static LspDiagnosticItem Item => new(new(new(0, 1), new(0, 2)), 1, "XG1005", "XamlG", "problem");
    private static ImmutableArray<LspDiagnosticItem> Items => ImmutableArray.Create(Item);

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("severity")]
    [InlineData("code")]
    [InlineData("source")]
    [InlineData("message")]
    public void EveryWireFieldParticipatesInEquality(string field)
    {
        var cache = new LspDiagnosticCache(); var original = cache.Report("a", Items);
        var changed = field switch
        {
            "start" => Item with { Range = new(new(0, 0), new(0, 2)) },
            "end" => Item with { Range = new(new(0, 1), new(0, 3)) },
            "severity" => Item with { Severity = 2 },
            "code" => Item with { Code = "OTHER" },
            "source" => Item with { Source = "Another compiler" },
            _ => Item with { Message = "different" }
        };
        var report = cache.Report("a", ImmutableArray.Create(changed), original.ResultId);
        Assert.Equal("full", report.Kind); Assert.NotEqual(original.ResultId, report.ResultId);
        Assert.Equal("unchanged", cache.Report("a", ImmutableArray.Create(changed), report.ResultId).Kind);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OrderAndRemovalAreVisibleChanges(bool reorder)
    {
        var first = Item; var second = Item with { Message = "second" };
        var cache = new LspDiagnosticCache(); var original = cache.Report("a", ImmutableArray.Create(first, second));
        var changed = reorder ? ImmutableArray.Create(second, first) : ImmutableArray.Create(first);
        var report = cache.Report("a", changed, original.ResultId);
        Assert.Equal("full", report.Kind); Assert.NotEqual(original.ResultId, report.ResultId);
        Assert.Equal(changed, report.Items!.Value);
    }
    [Theory]
    [InlineData("item")]
    [InlineData("code")]
    [InlineData("source")]
    [InlineData("message")]
    public void InvalidInputsCannotEvictOrReplaceAValidReport(string field)
    {
        var cache = new LspDiagnosticCache(capacity: 1); var original = cache.Report("a", Items);
        var cost = cache.RetainedCharacters;
        var invalid = field switch
        {
            "item" => null!,
            "code" => Item with { Code = null! },
            "source" => Item with { Source = null! },
            _ => Item with { Message = null! }
        };
        Assert.Throws<ArgumentException>(() => cache.Report("b", ImmutableArray.Create<LspDiagnosticItem>(invalid)));
        Assert.Equal(cost, cache.RetainedCharacters); Assert.Equal(1, cache.Count);
        Assert.Equal("unchanged", cache.Report("a", Items, original.ResultId).Kind);
    }
    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void ResultIdOverflowDoesNotMutateAnExistingEntryOrEvictAnother(string destination)
    {
        var cache = new LspDiagnosticCache(capacity: 1); var original = cache.Report("a", Items);
        var cost = cache.RetainedCharacters;
        typeof(LspDiagnosticCache).GetField("_sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cache, long.MaxValue);
        Assert.Throws<OverflowException>(() => cache.Report(destination, ImmutableArray.Create(Item with { Message = "changed" })));
        Assert.Equal(1, cache.Count); Assert.Equal(cost, cache.RetainedCharacters);
        Assert.Equal("unchanged", cache.Report("a", Items, original.ResultId).Kind);
    }
    [Fact]
    public void OversizedReplacementRetiresOnlyItsOwnOlderToken()
    {
        var cache = new LspDiagnosticCache(maximumCharacters: 400);
        var first = cache.Report("a", Items); var second = cache.Report("b", Items);
        var oversized = cache.Report("a", ImmutableArray.Create(Item with { Message = new string('x', 1000) }), first.ResultId);
        Assert.Null(oversized.ResultId); Assert.Equal("full", oversized.Kind);
        Assert.Equal(1, cache.Count); Assert.Equal("unchanged", cache.Report("b", Items, second.ResultId).Kind);
        Assert.Equal("full", cache.Report("a", Items, first.ResultId).Kind);
    }
    [Fact]
    public void EmptyOpaquePreviousIdsAreValidCacheMisses()
    {
        var cache = new LspDiagnosticCache(); var report = cache.Report("a", Items, string.Empty);
        Assert.Equal("full", report.Kind); Assert.NotEmpty(report.ResultId!);
        Assert.Equal("unchanged", cache.Report("a", Items, report.ResultId).Kind);
    }
    [Fact]
    public void DefaultAndExplicitEmptyArraysHaveIdenticalMeaning()
    {
        var cache = new LspDiagnosticCache(); var first = cache.Report("a", default);
        Assert.Empty(first.Items!.Value);
        var report = cache.Report("a", ImmutableArray<LspDiagnosticItem>.Empty, first.ResultId);
        Assert.Equal("unchanged", report.Kind); Assert.Null(report.Items);
    }
    [Fact]
    public void UnicodeReportsAndOldSnapshotsRemainUnmodified()
    {
        var cache = new LspDiagnosticCache(); var item = Item with { Message = "Zażółć gęślą jaźń · λ · 😀" };
        var first = cache.Report("a", ImmutableArray.Create(item));
        var changed = cache.Report("a", Items, first.ResultId);
        Assert.Equal(item, first.Items!.Value.Single()); Assert.Equal(Item, changed.Items!.Value.Single());
        var serialized = JsonSerializer.Serialize(first);
        var decoded = JsonSerializer.Deserialize<LspDocumentDiagnosticReport>(serialized)!;
        Assert.Equal(item, decoded.Items!.Value.Single());
    }
    [Fact]
    public void RemoveRetiresOnlyTheSelectedDocument()
    {
        var cache = new LspDiagnosticCache(); var a = cache.Report("a", Items); var b = cache.Report("b", Items);
        cache.Remove("a"); cache.Remove("a");
        Assert.Equal(1, cache.Count); Assert.Equal("unchanged", cache.Report("b", Items, b.ResultId).Kind);
        Assert.Equal("full", cache.Report("a", Items, a.ResultId).Kind);
        Assert.Throws<ArgumentNullException>(() => cache.Remove(null!));
    }
    [Fact]
    public void OversizedUriFailsBeforeCacheMutation()
    {
        var cache = new LspDiagnosticCache(capacity: 1); var before = cache.Report("a", Items);
        Assert.Throws<ArgumentException>(() => cache.Report(new string('x', 8193), Items));
        Assert.Equal("unchanged", cache.Report("a", Items, before.ResultId).Kind);
    }
}
