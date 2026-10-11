using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class SharedSourcePublisherTests
{
    [Fact]
    public void Stable_ownership_reuses_the_published_record_and_selection_snapshot()
    {
        var helper = new SharedGeneratedSource("Helper", "helper");
        var entry = Entry(Emission("base", helper));
        var first = Publish(entry);
        var snapshot = entry.PublishedSources;
        var second = Publish(entry);
        Assert.Same(first[0], second[0]);
        Assert.True(snapshot.Equals(entry.PublishedSources));
        Assert.Equal("base\nhelper", second[0].Source);
        Assert.Equal("base", entry.Output!.Source);
    }

    [Fact]
    public void Ownership_loss_shrink_growth_and_transfer_match_the_previous_publisher()
    {
        var a = new SharedGeneratedSource("A", "source A");
        var b = new SharedGeneratedSource("B", "source B");
        var c = new SharedGeneratedSource("C", "source C");
        var entries = new[] { Entry(Emission("first", a)), Entry(Emission("second", b)), Entry(Emission("third", a, b, c)) };
        var legacy = entries.Select(entry => new LegacyEntry(entry.Output!)).ToArray();
        foreach (var order in new[] { new[] { 2 }, new[] { 0, 2 }, new[] { 0, 1, 2 }, new[] { 1, 2 }, new[] { 2 }, new[] { 2 } })
            Compare(entries, legacy, order);
    }

    [Fact]
    public void Losing_all_helpers_does_not_poison_the_remembered_output()
    {
        var helper = new SharedGeneratedSource("A", "helper");
        var earlier = Entry(Emission("earlier", helper));
        var entry = Entry(Emission("base", helper));
        var initial = Publish(entry)[0];
        var lost = Publish(earlier, entry);
        Assert.Same(entry.Output, lost[1]);
        Assert.Same(initial, Publish(entry)[0]);
    }

    [Fact]
    public void First_successful_occurrence_wins_even_with_repeated_identities()
    {
        var a = new SharedGeneratedSource("A", "first\nbody");
        var repeat = new SharedGeneratedSource("A", "must not publish");
        var empty = new SharedGeneratedSource("B", "");
        var bad = Entry(Emission("bad", repeat) with { Diagnostics = [new("bad", "error", default)] });
        var good = Entry(Emission("good", a, repeat, empty));
        var later = Entry(Emission("later", repeat));
        var outputs = Publish(bad, good, later);
        Assert.Same(bad.Output, outputs[0]);
        Assert.Equal("good\nfirst\nbody\n", outputs[1].Source);
        Assert.Same(later.Output, outputs[2]);
    }

    [Fact]
    public void Non_cached_emission_does_not_replace_the_entrys_cached_publication()
    {
        var a = new SharedGeneratedSource("A", "source A");
        var b = new SharedGeneratedSource("B", "source B");
        var entry = Entry(Emission("cached", a));
        var remembered = Publish(entry)[0];
        var original = entry.PublishedSources;
        var uncached = new[] { Emission("temporary", b) };
        SharedSourcePublisher.Publish(uncached, [entry], default);
        Assert.Equal("temporary\nsource B", uncached[0].Source);
        Assert.Same(remembered, entry.OutputWithHelpers);
        Assert.True(original.Equals(entry.PublishedSources));
        Assert.Same(remembered, Publish(entry)[0]);
    }

    [Fact]
    public void Seeded_owner_changes_match_the_frozen_publisher()
    {
        var random = new Random(29073);
        var helpers = Enumerable.Range(0, 12).Select(i => new SharedGeneratedSource("H" + i, "body\n" + i)).ToArray();
        var entries = Enumerable.Range(0, 24).Select(i => Entry(Emission("base" + i,
            Enumerable.Range(0, random.Next(0, 15)).Select(_ => helpers[random.Next(helpers.Length)]).ToArray()))).ToArray();
        var legacy = entries.Select(entry => new LegacyEntry(entry.Output!)).ToArray();
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var order = Enumerable.Range(0, entries.Length).Where(_ => random.Next(3) != 0).ToArray();
            Compare(entries, legacy, order);
            // Exercise the stable path immediately after every ownership transition.
            var first = Publish(order.Select(i => entries[i]).ToArray());
            var second = Publish(order.Select(i => entries[i]).ToArray());
            for (var i = 0; i < first.Length; i++) Assert.Same(first[i], second[i]);
        }
    }

    [Fact]
    public void Cancelled_publication_does_not_change_cached_ownership()
    {
        var entry = Entry(Emission("base", new SharedGeneratedSource("Helper", "helper")));
        var first = Publish(entry)[0];
        var selection = entry.PublishedSources;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var outputs = new[] { entry.Output! };
        Assert.Throws<OperationCanceledException>(() => SharedSourcePublisher.Publish(outputs, [entry], cancellation.Token));
        Assert.Same(entry.Output, outputs[0]);
        Assert.Same(first, entry.OutputWithHelpers);
        Assert.True(selection.Equals(entry.PublishedSources));
        Assert.Same(first, Publish(entry)[0]);
    }

    [Fact]
    public void No_helper_outputs_are_returned_without_wrappers()
    {
        var empty = Entry(Emission("body"));
        var skipped = Entry(Emission("") with { IsSkipped = true });
        var outputs = Publish(empty, skipped);
        Assert.Same(empty.Output, outputs[0]);
        Assert.Same(skipped.Output, outputs[1]);
        Assert.Null(empty.OutputWithHelpers);
        Assert.Null(skipped.OutputWithHelpers);
    }

    private static void Compare(CachedProjectDocument[] entries, LegacyEntry[] legacy, int[] order)
    {
        var current = Publish(order.Select(i => entries[i]).ToArray());
        var expected = Legacy(order.Select(i => legacy[i]).ToArray());
        for (var i = 0; i < current.Length; i++)
        {
            Assert.Equal(expected[i].Source, current[i].Source);
            Assert.Equal(expected[i].Diagnostics.ToArray(), current[i].Diagnostics.ToArray());
            Assert.True(expected[i].SharedSources.Equals(current[i].SharedSources));
        }
    }
    private static XamlEmissionResult[] Publish(params CachedProjectDocument[] entries)
    {
        var outputs = entries.Select(entry => entry.Output!).ToArray();
        SharedSourcePublisher.Publish(outputs, entries, default);
        return outputs;
    }
    private static XamlEmissionResult Emission(string source, params SharedGeneratedSource[] helpers) =>
        new("generated", source, "Factory", "Build", "Populate", [], []) { SharedSources = helpers.ToImmutableArray() };
    private static CachedProjectDocument Entry(XamlEmissionResult output)
    {
        var syntax = XamlSyntaxTree.Parse("<Node/>", "View.xaml");
        var document = new BoundDocument(syntax, null, null, null, "public", [], [], XamlFrameworkProfile.Portable, new());
        return new(new(syntax, "View.xaml"), document) { Output = output };
    }

    private sealed class LegacyEntry(XamlEmissionResult output)
    {
        public XamlEmissionResult Output { get; } = output;
        public string? Identity { get; set; }
        public XamlEmissionResult? Published { get; set; }
    }
    // Frozen b686b73 publication loop: separate legacy state and LINQ selection.
    private static XamlEmissionResult[] Legacy(LegacyEntry[] entries)
    {
        var emissions = entries.Select(entry => entry.Output).ToArray();
        var published = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < emissions.Length; i++)
        {
            var emission = emissions[i];
            if (!emission.Success) continue;
            var owned = emission.SharedSources.Where(source => published.Add(source.Identity)).ToArray();
            if (owned.Length == 0) continue;
            var identity = string.Join("\n", owned.Select(source => source.Identity));
            var entry = entries[i];
            if (entry.Identity != identity)
            {
                entry.Identity = identity;
                entry.Published = emission with { Source = emission.Source + "\n" + string.Join("\n", owned.Select(source => source.Source)) };
            }
            emissions[i] = entry.Published!;
        }
        return emissions;
    }
}
