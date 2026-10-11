using System.Globalization;
using System.Text;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class WhitespaceFastPathTests
{
    // Frozen public contract: XML whitespace, not char.IsWhiteSpace or String.Trim.
    internal static string Legacy(string text, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length); var pending = false;
        foreach (var c in text)
        {
            if (XmlWhitespace.IsWhitespace(c)) { pending = true; continue; }
            if (pending && (output.Length > 0 || !trimStart)) output.Append(' ');
            pending = false; output.Append(c);
        }
        if (pending && !trimEnd && (output.Length > 0 || !trimStart)) output.Append(' ');
        return output.ToString();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Exhaustive_short_inputs_match_frozen_collapse(bool trimStart, bool trimEnd)
    {
        const string alphabet = "a \t\r\n\u00a0";
        void Visit(string value, int remaining)
        {
            var expected = Legacy(value, trimStart, trimEnd);
            var actual = XmlWhitespace.Collapse(value, trimStart, trimEnd);
            Assert.Equal(expected, actual);
            Assert.Equal(actual, XmlWhitespace.Collapse(actual, trimStart, trimEnd));
            if (remaining != 0) foreach (var character in alphabet) Visit(value + character, remaining - 1);
        }
        Visit(string.Empty, 5);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Long_random_utf16_and_whitespace_runs_match_legacy(bool trimStart, bool trimEnd)
    {
        var random = new Random(6193);
        const string characters = "ab \t\r\n\0\u00a0\u2028\u2029\u2003\ud83d\ude00";
        for (var sample = 0; sample < 128; sample++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1024)).Select(_ => characters[random.Next(characters.Length)]).ToArray());
            Assert.Equal(Legacy(text, trimStart, trimEnd), XmlWhitespace.Collapse(text, trimStart, trimEnd));
        }
        foreach (var text in new[] { new string('\t', 131072), "x" + new string(' ', 65536) + "y", new string('x', 65536) + " \r\n" })
            Assert.Equal(Legacy(text, trimStart, trimEnd), XmlWhitespace.Collapse(text, trimStart, trimEnd));
    }

    [Fact]
    public void Canonical_and_preserved_text_reuse_the_input_without_normalizing_non_xml_spaces()
    {
        foreach (var text in new[] { "alpha", "alpha beta", "\u00a0x\u2003y\u2028\u2029", "\0\ud83d\ude00" })
            Assert.Same(text, XmlWhitespace.Collapse(text, true, true));
        var bounded = new string(" alpha beta ".ToCharArray());
        Assert.Same(bounded, XmlWhitespace.Collapse(bounded, false, false));
        var raw = new string(" \r\ntext\t ".ToCharArray());
        Assert.Same(raw, XmlWhitespace.Normalize(raw, preserve: true));
        Assert.Equal("text", XmlWhitespace.Normalize(raw, preserve: false));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("pl-PL")]
    public void Collapse_is_culture_independent(string culture)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal("I \u00a0 i", XmlWhitespace.Collapse("\tI \u00a0\r\ni\t", true, true));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
