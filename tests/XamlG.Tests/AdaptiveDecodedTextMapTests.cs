using System.Reflection;
using System.Text;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class AdaptiveDecodedTextMapTests
{
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(129)]
    public void Dense_admission_and_late_density_reversal_preserve_every_boundary(int count)
    {
        var dense = string.Concat(Enumerable.Repeat("&amp;&#x10000;\r\n", count));
        var plain = new string('p', 4096);
        Check(dense + plain);
        Check(plain + dense);
        Check(dense + plain + dense + "\t\r\0\ud800");
    }

    [Fact]
    public void Dense_first_then_sparse_output_compacts_without_retaining_a_direct_table()
    {
        var raw = string.Concat(Enumerable.Repeat("&amp;", 64)) + new string('x', 65536);
        var map = XamlDecodedTextMap.Create(raw, new(0, raw.Length));
        var storage = Storage(map)!;
        Assert.Equal(64, storage.Length);
        Assert.Equal(new TextSpan(320, 65536), map.ToSource(new(64, 65536)));
        Check(raw);
    }

    [Theory]
    [InlineData("&unfinished")]
    [InlineData("&unknown;")]
    [InlineData("&#xD800;")]
    public void Dense_continuation_retains_malformed_entity_contracts(string invalid)
    {
        var raw = string.Concat(Enumerable.Repeat("&amp;", 64)) + invalid;
        foreach (var attribute in new[] { false, true })
        {
            var expected = invalid == "&unfinished"
                ? new ArgumentException("Invalid XML entity in the source range.", "source")
                : new ArgumentException("Invalid XML character entity.", "entity");
            var actual = Assert.Throws<ArgumentException>(() => XamlDecodedTextMap.Create(raw, new(0, raw.Length), attribute));
            Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    private static Array? Storage(XamlDecodedTextMap map) =>
        (Array?)typeof(XamlDecodedTextMap).GetField("_boundaries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map);

    private static void Check(string raw)
    {
        foreach (var attribute in new[] { false, true })
        {
            // Independent per-character reference for this fixture's alphabet;
            // no candidate decoder, checkpoint helper or density decision is used.
            var expected = new StringBuilder();
            var boundaries = new List<int> { 0 };
            for (var i = 0; i < raw.Length;)
            {
                if (raw[i] == '&')
                {
                    if (raw.AsSpan(i).StartsWith("&amp;".AsSpan()))
                    { expected.Append('&'); i += 5; }
                    else if (raw.AsSpan(i).StartsWith("&#x10000;".AsSpan()))
                    { expected.Append("\ud800\udc00"); boundaries.Add(-1); i += 9; }
                    else throw new InvalidOperationException("Unexpected reference-fixture token.");
                }
                else if (raw[i] == '\r')
                { i++; if (i < raw.Length && raw[i] == '\n') i++; expected.Append(attribute ? ' ' : '\n'); }
                else
                { var c = raw[i++]; expected.Append(attribute && c is '\n' or '\t' ? ' ' : c); }
                boundaries.Add(i);
            }
            var actual = XamlDecodedTextMap.Create("prefix:" + raw + ":suffix", new(7, raw.Length), attribute);
            Assert.Equal(expected.ToString(), actual.Text);
            for (var i = 0; i < boundaries.Count; i++)
            {
                if (boundaries[i] < 0)
                {
                    Assert.Throws<ArgumentException>(() => actual.ToSource(new(i, 0)));
                    Assert.Throws<ArgumentException>(() => actual.ToSource(new(0, i)));
                    Assert.Throws<ArgumentException>(() => actual.ToSource(new(i, actual.Text.Length - i)));
                }
                else
                {
                    Assert.Equal(new TextSpan(7 + boundaries[i], 0), actual.ToSource(new(i, 0)));
                    Assert.Equal(new TextSpan(7, boundaries[i]), actual.ToSource(new(0, i)));
                    Assert.Equal(new TextSpan(7 + boundaries[i], raw.Length - boundaries[i]),
                        actual.ToSource(new(i, actual.Text.Length - i)));
                }
            }
        }
    }
}
