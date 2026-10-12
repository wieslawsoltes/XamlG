using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class FingerprintBufferTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(5459)]
    [InlineData(5460)]
    [InlineData(5461)]
    [InlineData(16384)]
    [InlineData(65536)]
    [InlineData(1048576)]
    public void Small_and_streaming_paths_match_previous_bytes_and_digest(int length)
    {
        var text = MakeText(length);
        using var hash = SHA256.Create();
        byte[] buffer = [];
        foreach (var prefix in new string?[] { null, "", "global::Model.View", "\ud800", "prefix\0\u6f22\ud83d\ude80" })
        {
            var source = "padding:" + text + ":end";
            var span = new TextSpan(8, text.Length);
            Assert.Equal(Previous(prefix, source, span), SourceFingerprint.Compute(hash, ref buffer, prefix, source, span));
            Assert.InRange(buffer.Length, 0, SourceFingerprint.BufferLimit);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5459)]
    [InlineData(5460)]
    [InlineData(5461)]
    public void Internal_chunk_boundaries_never_split_valid_pairs(int relative)
    {
        var at = SourceFingerprint.ChunkCharacters + relative;
        using var hash = SHA256.Create();
        byte[] buffer = [];
        foreach (var middle in new[] { "\ud83d\ude80", "\ud800X", "\udfff", "\ud800\ud800\udfff", "\0" })
        {
            var text = new string('a', at) + middle + new string('\u6f22', 18000);
            foreach (var prefix in new string?[] { null, "", "type" })
                Assert.Equal(Previous(prefix, text, new(0, text.Length)),
                    SourceFingerprint.Compute(hash, ref buffer, prefix, text, new(0, text.Length)));
        }
    }

    [Fact]
    public void Prefix_and_slice_boundaries_keep_independent_replacement_fallback()
    {
        using var hash = SHA256.Create();
        byte[] buffer = [];
        var text = "\ud83d\ude80" + new string('x', 65536) + "\ud83d\ude80";
        foreach (var span in new[] { new TextSpan(1, text.Length - 2), new TextSpan(0, 1), new TextSpan(text.Length - 1, 1), new TextSpan(2, text.Length - 3) })
        foreach (var prefix in new string?[] { null, "", "\ud800", new string('p', 65536) + "\ud800" })
            Assert.Equal(Previous(prefix, text, span), SourceFingerprint.Compute(hash, ref buffer, prefix, text, span));
        Assert.NotEqual(SourceFingerprint.Compute(hash, ref buffer, null, "", default),
            SourceFingerprint.Compute(hash, ref buffer, "", "", default));
    }

    [Fact]
    public void Every_UTF16_code_unit_and_seeded_slices_match_the_frozen_implementation()
    {
        var text = new string(Enumerable.Range(0, 65536).Select(value => (char)value).ToArray());
        var random = new Random(718231);
        using var hash = SHA256.Create();
        byte[] buffer = [];
        for (var i = 0; i < 200; i++)
        {
            var start = random.Next(text.Length + 1);
            var span = new TextSpan(start, random.Next(text.Length - start + 1));
            var prefix = i % 3 == 0 ? null : text.Substring(random.Next(64000), i % 17);
            Assert.Equal(Previous(prefix, text, span), SourceFingerprint.Compute(hash, ref buffer, prefix, text, span));
        }
        Assert.Equal(Previous(null, text, new(0, text.Length)), SourceFingerprint.Compute(hash, ref buffer, null, text, new(0, text.Length)));
    }

    [Fact]
    public void Increasing_requests_have_geometric_scratch_allocation_and_bounded_retention()
    {
        using var hash = SHA256.Create();
        byte[] buffer = [];
        var source = new string('x', 1 << 20);
        var allocations = 0;
        long totalCapacity = 0;
        for (var length = 1; length <= 6000; length += 17)
        {
            var previous = buffer;
            SourceFingerprint.Compute(hash, ref buffer, null, source, new(0, length));
            if (!ReferenceEquals(previous, buffer)) { allocations++; totalCapacity += buffer.Length; }
            Assert.InRange(buffer.Length, 1, SourceFingerprint.BufferLimit);
        }
        Assert.InRange(allocations, 1, 20);
        Assert.True(totalCapacity <= 3L * SourceFingerprint.BufferLimit, totalCapacity.ToString());
        var largest = buffer;
        Assert.Equal(Previous(null, source, new(0, source.Length)), SourceFingerprint.Compute(hash, ref buffer, null, source, new(0, source.Length)));
        Assert.Same(largest, buffer);
    }

    [Fact]
    public void Large_small_empty_and_invalid_requests_do_not_poison_reused_hash_state()
    {
        using var hash = SHA256.Create();
        byte[] buffer = [];
        var large = MakeText(100000);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(Previous("type", large, new(0, large.Length)), SourceFingerprint.Compute(hash, ref buffer, "type", large, new(0, large.Length)));
            Assert.Equal("ba7816bf8f01cfea414140de", SourceFingerprint.Compute(hash, ref buffer, null, "abc", new(0, 3)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SourceFingerprint.Compute(hash, ref buffer, "type", "abc", new(4, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SourceFingerprint.Compute(hash, ref buffer, "type", "abc", new(1, 3)));
            Assert.Equal(Previous(null, "", default), SourceFingerprint.Compute(hash, ref buffer, null, "", default));
        }
    }

    [Fact]
    public void Streaming_failure_restores_provider_state_for_subsequent_hashes()
    {
        using var hash = new FailingSha256();
        byte[] buffer = [];
        var text = new string('x', 65536);
        Assert.Throws<InvalidOperationException>(() => SourceFingerprint.Compute(hash, ref buffer, "type", text, new(0, text.Length)));
        Assert.Equal(Previous("type", text, new(0, text.Length)), SourceFingerprint.Compute(hash, ref buffer, "type", text, new(0, text.Length)));
        Assert.Equal(Previous(null, "abc", new(0, 3)), SourceFingerprint.Compute(hash, ref buffer, null, "abc", new(0, 3)));
    }

    [Fact]
    public void Context_disposal_releases_scratch_and_independent_contexts_are_deterministic()
    {
        var document = new EmissionAnalysisFixture().Document;
        var text = MakeText(100000);
        var expected = Previous("type", text, new(0, text.Length));
        var field = typeof(EmissionContext).GetField("_hashInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Parallel.For(0, 32, _ =>
        {
            using var context = new EmissionContext(document, default);
            Assert.Equal(expected, context.StableId("type", text, new(0, text.Length)));
            Assert.InRange(((byte[])field.GetValue(context)!).Length, 1, SourceFingerprint.BufferLimit);
            context.Dispose();
            Assert.Empty((byte[])field.GetValue(context)!);
        });
    }

    private static string MakeText(int length)
    {
        const string alphabet = "AZ\0\t\u00e9\u6f22\ud83d\ude80\ud800X\udfff";
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[i % alphabet.Length];
        return new(chars);
    }

    // Frozen old input assembly; neither chunking nor the new hex helper participates.
    private static string Previous(string? prefix, string value, TextSpan span)
    {
        var encoding = Encoding.UTF8;
        var capacity = (prefix == null ? 0 : encoding.GetByteCount(prefix) + 1) + encoding.GetMaxByteCount(span.Length);
        var bytes = new byte[capacity];
        var offset = 0;
        if (prefix != null)
        {
            offset = encoding.GetBytes(prefix, 0, prefix.Length, bytes, 0);
            bytes[offset++] = 0;
        }
        var written = encoding.GetBytes(value, span.Start, span.Length, bytes, offset);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(bytes, 0, offset + written), 0, 12).ToLowerInvariant();
    }

    private sealed class FailingSha256 : SHA256
    {
        private readonly SHA256 _inner = SHA256.Create();
        private int _calls;
        private bool _fail = true;
        public override void Initialize() { _inner.Initialize(); _calls = 0; }
        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            if (_fail && ++_calls == 2) { _fail = false; throw new InvalidOperationException("injected streaming failure"); }
            _inner.TransformBlock(array, ibStart, cbSize, array, ibStart);
        }
        protected override byte[] HashFinal()
        {
            _inner.TransformFinalBlock([], 0, 0);
            return _inner.Hash!;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
