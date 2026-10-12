using System.Globalization;
using System.Reflection;
using System.Text;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class SparseDecodedTextMapTests
{
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(256)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void Sparse_entities_and_line_endings_preserve_every_boundary(int length)
    {
        foreach (var position in new[] { 0, length / 2, length })
        foreach (var entity in new[] { "&amp;", "&#x1F600;", "\r\n", "\r", "\t" })
            Check(new string('a', position) + entity + new string('z', length - position));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(1024)]
    public void Dense_and_late_dense_changes_keep_indivisible_entities(int count)
    {
        var raw = string.Concat(Enumerable.Repeat("&amp;&#x10000;\r\n&#9;", count));
        Check(raw);
        Check(new string('x', 129) + raw);
        Check(raw + new string('x', 4096));
    }

    [Fact]
    public void Seeded_UTF16_documents_match_the_per_character_oracle()
    {
        string[] pieces = ["a", " \t", "\r", "\r\n", "\n", "\0", "\ud800", "\udfff", "😀", "\u00a0",
            "&amp;", "&lt;", "&gt;", "&apos;", "&quot;", "&#9;", "&#13;", "&#xD7FF;", "&#xE000;",
            "&#x10000;", "&#x10FFFF;", "&#000000000000000000000000000032;"];
        var random = new Random(107731);
        for (var sample = 0; sample < 250; sample++)
            Check(string.Concat(Enumerable.Range(0, random.Next(1, 100)).Select(_ => pieces[random.Next(pieces.Length)])));
    }

    [Theory]
    [InlineData("&unfinished")]
    [InlineData("&unknown;")]
    [InlineData("&#0;")]
    [InlineData("&#xD800;")]
    [InlineData("&#x110000;")]
    [InlineData("&#X41;")]
    [InlineData("&#00000000000000000000000000000000032;")]
    public void Invalid_entities_keep_exception_type_parameter_and_message(string invalid)
    {
        foreach (var prefix in new[] { "", new string('x', 129), new string('x', 4096) })
        foreach (var attribute in new[] { false, true })
        {
            var raw = prefix + invalid;
            var expected = Assert.Throws<ArgumentException>(() => ReferenceMap.Create(raw, new(0, raw.Length), attribute));
            var actual = Assert.Throws<ArgumentException>(() => XamlDecodedTextMap.Create(raw, new(0, raw.Length), attribute));
            Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    [Fact]
    public void Sparse_storage_is_bounded_by_offset_changes_not_plain_text_length()
    {
        var text = new string('x', 65536);
        var map = XamlDecodedTextMap.Create(text + "&amp;" + text, new(0, text.Length * 2 + 5));
        Assert.Single(Storage(map)!);
        var pair = XamlDecodedTextMap.Create(text + "&#x10000;" + text, new(0, text.Length * 2 + 9));
        Assert.Equal(2, Storage(pair)!.Length);
        var normalized = XamlDecodedTextMap.Create(text + "\t\r\n", new(0, text.Length + 3));
        Assert.Single(Storage(normalized)!); // Only CRLF changes the offset.
        var sameLength = XamlDecodedTextMap.Create(text + "\t\r\n", new(0, text.Length + 2));
        Assert.Null(Storage(sameLength)); // TAB and lone CR change text, not coordinates.
    }

    [Fact]
    public void Dense_representation_remains_direct_and_does_not_exceed_old_payload_size()
    {
        var raw = string.Concat(Enumerable.Repeat("&amp;", 1024));
        var map = XamlDecodedTextMap.Create(raw, new(0, raw.Length));
        Assert.Equal(map.Text.Length + 1, Assert.IsType<int[]>(Storage(map)).Length);
        Assert.IsType<int[]>(Storage(XamlDecodedTextMap.Create("a&amp;b", new(0, 7))));
    }

    [Fact]
    public void Concurrent_queries_share_only_immutable_owned_storage()
    {
        var raw = new string('x', 512) + "&#x1F600;" + new string('y', 512) + "&amp;";
        var map = XamlDecodedTextMap.Create("prefix:" + raw, new(7, raw.Length));
        var reference = ReferenceMap.Create("prefix:" + raw, new(7, raw.Length), true);
        Parallel.For(0, 128, _ => CompareBoundaries(reference, map));
    }

    [Fact]
    public void Large_nested_markup_ranges_map_to_exact_raw_entities()
    {
        var rawValue = new string('x', 256) + "&amp;&#x1F600;";
        var source = "<Root Value=\"{Wrap Prefix=&quot;" + new string('p', 256) + "&quot;, Value={x:Reference Name='" + rawValue + "'}}\"/>";
        var tree = XamlSyntaxTree.Parse(source);
        Assert.Empty(tree.Diagnostics);
        var occurrence = XamlMarkupScanner.Scan(tree, tree.Root!.Attributes[0]).Single(x => x.Syntax.Name == "x:Reference");
        var argument = Assert.Single(occurrence.Syntax.Arguments);
        var range = occurrence.SourceMap.ToSource(argument.ValueSpan!.Value);
        Assert.Equal(rawValue, source.Substring(range.Start, range.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_encoded_markup_executes_in_standalone_and_shared_factories(bool shared)
    {
        const string model = """
            using System;
            namespace SparseMapFixture {
                public class Root { public string Text { get; set; } = ""; }
                public class EchoExtension {
                    public string Text { get; set; } = "";
                    public string ProvideValue(IServiceProvider provider) => Text;
                }
            }
            """;
        var plain = new string('x', 1024);
        var raw = "<Root xmlns='clr-namespace:SparseMapFixture' Text='{Echo Text=&quot;" + plain + "&amp;&#x1F600;&quot;}'/>";
        using var code = CompiledXaml.Create(raw, model, shareAcrossDocuments: shared);
        for (var i = 0; i < 2; i++)
        {
            var root = code.Build();
            try { Assert.Equal(plain + "&😀", root.GetType().GetProperty("Text")!.GetValue(root)); }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
    }

    private static Array? Storage(XamlDecodedTextMap map) =>
        (Array?)typeof(XamlDecodedTextMap).GetField("_boundaries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map);

    private static void Check(string raw)
    {
        foreach (var attribute in new[] { false, true })
        {
            var source = "prefix:" + raw + ":suffix";
            CompareBoundaries(ReferenceMap.Create(source, new(7, raw.Length), attribute),
                XamlDecodedTextMap.Create(source, new(7, raw.Length), attribute));
        }
    }

    private static void CompareBoundaries(ReferenceMap expected, XamlDecodedTextMap actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        for (var position = 0; position <= expected.Text.Length; position++)
        {
            if (!expected.IsBoundary(position))
            {
                var error = Assert.Throws<ArgumentException>(() => actual.ToSource(new(position, 0)));
                Assert.Equal("decodedSpan", error.ParamName);
                Assert.Throws<ArgumentException>(() => actual.ToSource(new(0, position)));
                Assert.Throws<ArgumentException>(() => actual.ToSource(new(position, expected.Text.Length - position)));
            }
            else
            {
                Assert.Equal(expected.ToSource(new(position, 0)), actual.ToSource(new(position, 0)));
                Assert.Equal(expected.ToSource(new(0, position)), actual.ToSource(new(0, position)));
                Assert.Equal(expected.ToSource(new(position, expected.Text.Length - position)), actual.ToSource(new(position, expected.Text.Length - position)));
            }
        }
        Assert.Throws<ArgumentException>(() => actual.ToSource(new(actual.Text.Length + 1, 0)));
        Assert.Throws<ArgumentException>(() => actual.ToSource(new(actual.Text.Length, 1)));
    }

    // Frozen per-character boundary algorithm; scalar decoding uses the unchanged
    // BCL integer contract rather than candidate checkpoints or map lookups.
    private sealed record ReferenceMap(string Text, int[] Boundaries, int Start)
    {
        public bool IsBoundary(int position) => Boundaries[position] >= 0;
        public TextSpan ToSource(TextSpan range) => TextSpan.FromBounds(Start + Boundaries[range.Start], Start + Boundaries[range.End]);
        public static ReferenceMap Create(string source, TextSpan span, bool attribute)
        {
            var text = source.AsSpan(span.Start, span.Length);
            var output = new StringBuilder(text.Length);
            var boundaries = new List<int> { 0 };
            for (var i = 0; i < text.Length;)
            {
                if (text[i] == '&')
                {
                    var end = text.Slice(i + 1, Math.Min(32, text.Length - i - 1)).IndexOf(';');
                    if (end < 0) throw new ArgumentException("Invalid XML entity in the source range.", nameof(source));
                    var entity = text.Slice(i + 1, end);
                    if (!Scalar(entity, out var scalar)) throw new ArgumentException("Invalid XML character entity.", nameof(entity));
                    i += end + 2;
                    if (scalar <= 0xFFFF) output.Append((char)scalar);
                    else
                    {
                        scalar -= 0x10000;
                        output.Append((char)(0xD800 + (scalar >> 10))).Append((char)(0xDC00 + (scalar & 0x3FF)));
                        boundaries.Add(-1);
                    }
                }
                else if (text[i] == '\r')
                { i++; if (i < text.Length && text[i] == '\n') i++; output.Append(attribute ? ' ' : '\n'); }
                else
                { var c = text[i++]; output.Append(attribute && c is '\n' or '\t' ? ' ' : c); }
                boundaries.Add(i);
            }
            return new(output.ToString(), boundaries.ToArray(), span.Start);
        }
        private static bool Scalar(ReadOnlySpan<char> entity, out int value)
        {
            var name = entity.ToString();
            value = name switch { "lt" => '<', "gt" => '>', "amp" => '&', "apos" => '\'', "quot" => '"', _ => -1 };
            if (value >= 0) return true;
            if (!name.StartsWith('#')) return false;
            var hex = name.StartsWith("#x", StringComparison.Ordinal);
            return int.TryParse(name.AsSpan(hex ? 2 : 1), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out value) &&
                (value is 9 or 10 or 13 || value is >= 0x20 and <= 0xD7FF || value is >= 0xE000 and <= 0xFFFD || value is >= 0x10000 and <= 0x10FFFF);
        }
    }
}
