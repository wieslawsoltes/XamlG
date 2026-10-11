using System.Buffers;
using System.Text;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class WhitespaceBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(4096)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(131073)]
    public void Boundary_lengths_preserve_all_trim_modes_and_UTF16(int length)
    {
        var random = new Random(length + 7421);
        const string alphabet = "ab \t\r\n\u00a0\u2003\ud800\udfff\0";
        var characters = new char[length];
        for (var i = 0; i < length; i++) characters[i] = alphabet[random.Next(alphabet.Length)];
        CheckAll(new string(characters));
        CheckAll(new string(' ', length));
        CheckAll(new string('x', length));
        if (length < 3) return;
        foreach (var position in new[] { 0, length / 2, length - 2 })
        {
            Array.Fill(characters, 'x');
            characters[position] = '\t'; characters[position + 1] = '\r';
            CheckAll(new string(characters));
        }
    }

    [Fact]
    public void Exhaustive_short_strings_match_original_pending_space_loop()
    {
        const string alphabet = "a \t\r\n\u00a0";
        for (var length = 0; length <= 6; length++)
        {
            var count = 1;
            for (var i = 0; i < length; i++) count *= alphabet.Length;
            var text = new char[length];
            for (var spelling = 0; spelling < count; spelling++)
            {
                var value = spelling;
                for (var i = 0; i < length; i++) { text[i] = alphabet[value % alphabet.Length]; value /= alphabet.Length; }
                CheckAll(new string(text));
            }
        }
    }

    [Fact]
    public void Canonical_strings_and_preserve_space_keep_original_identity()
    {
        foreach (var length in new[] { 0, 256, 257, 16384, 16385 })
        {
            var text = new string('x', length);
            for (var mask = 0; mask < 4; mask++) Assert.Same(text, XmlWhitespace.Collapse(text, (mask & 1) != 0, (mask & 2) != 0));
            var raw = " \t" + text + "\r\n ";
            Assert.Same(raw, XmlWhitespace.Normalize(raw, preserve: true));
        }
    }

    [Fact]
    public void Reused_pool_storage_cannot_leak_into_shorter_output()
    {
        // Deliberately fill a same-thread bucket before using the production path.
        var rental = ArrayPool<char>.Shared.Rent(4096);
        Array.Fill(rental, '\uffff');
        ArrayPool<char>.Shared.Return(rental);
        var source = "alpha\t" + new string(' ', 4000) + "omega\r\n";
        for (var iteration = 0; iteration < 100; iteration++)
        {
            Assert.Equal("alpha omega", XmlWhitespace.Collapse(source, true, true));
            Assert.Equal("x y", XmlWhitespace.Collapse("x" + new string('\t', 2048) + "y", true, true));
        }
    }

    [Fact]
    public void Parallel_calls_do_not_share_mutable_results()
    {
        Parallel.For(0, 128, i =>
        {
            var text = i + "\t" + new string('z', 300 + i) + "\r\n\ud800";
            CheckAll(text);
        });
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(4096)]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(65536)]
    public void Dense_and_long_run_transitions_keep_the_original_trim_state(int length)
    {
        const string dense = "ab\t\r\n c\u00a0\ud800\udfff";
        foreach (var longFirst in new[] { false, true })
        {
            var text = new char[length];
            for (var i = 0; i < text.Length; i++)
                text[i] = (i < length / 2) == longFirst ? 'x' : dense[i % dense.Length];
            text[0] = '\t'; text[length - 1] = '\r';
            CheckAll(new string(text));
        }
    }

    [Fact]
    public void Bulk_copy_promotion_and_scalar_fallback_preserve_pending_spaces()
    {
        foreach (var count in new[] { 15, 16, 17 })
        foreach (var prefix in new[] { "", "prefix" })
        foreach (var suffix in new[] { "", "\t", "\tq", "\r\nq\t" + new string('y', 40) })
            CheckAll(prefix + "\t" + new string('x', count) + suffix);
    }

    private static void CheckAll(string text)
    {
        for (var mask = 0; mask < 4; mask++)
        {
            var trimStart = (mask & 1) != 0; var trimEnd = (mask & 2) != 0;
            Assert.Equal(Legacy(text, trimStart, trimEnd), XmlWhitespace.Collapse(text, trimStart, trimEnd));
        }
    }

    // Frozen pre-optimization state machine: no candidate tokenization or buffers.
    private static string Legacy(string text, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length);
        var pending = false;
        foreach (var character in text)
        {
            if (character is ' ' or '\t' or '\n' or '\r') { pending = true; continue; }
            if (pending && (output.Length != 0 || !trimStart)) output.Append(' ');
            pending = false;
            output.Append(character);
        }
        if (pending && !trimEnd && (output.Length != 0 || !trimStart)) output.Append(' ');
        return output.ToString();
    }
}
