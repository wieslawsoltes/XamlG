using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class WhitespaceTransitionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Changed_run_handoff_preserves_prefix_and_pending_boundary_state(bool trimStart, bool trimEnd)
    {
        // Exercise the transition from the no-copy scan into the changed suffix,
        // not only strings whose first code unit already needs normalization.
        foreach (var length in new[] { 0, 1, 16, 4096 })
        foreach (var prefix in new[] { new string('x', length), string.Concat(Enumerable.Repeat("a b ", length)) + "z" })
        foreach (var run in new[] { " ", "  ", "\t", "\r\n", new string(' ', 1024) })
        foreach (var suffix in new[] { "", "y", " y ", "y\t \nz\r\n", "\u00a0\0\ud83d\ude00", "y" + new string('z', 4096) })
        {
            var text = prefix + run + suffix;
            var expected = WhitespaceFastPathTests.Legacy(text, trimStart, trimEnd);
            var actual = XmlWhitespace.Collapse(text, trimStart, trimEnd);
            Assert.Equal(expected, actual);
            Assert.Equal(actual, XmlWhitespace.Collapse(actual, trimStart, trimEnd));
            Assert.Same(text, XmlWhitespace.Normalize(text, preserve: true));
        }
    }
}
