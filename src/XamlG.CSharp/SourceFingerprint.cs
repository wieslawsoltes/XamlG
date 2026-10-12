using System.Security.Cryptography;
using System.Text;
using XamlG.Syntax;

namespace XamlG.CSharp;

/// <summary>Emission-owned SHA-256 scratch space with bounded retention. Hash inputs
/// and the 96-bit identifier spelling are unchanged; only buffering is different.</summary>
internal static class SourceFingerprint
{
    internal const int BufferLimit = 16 * 1024;
    internal const int ChunkCharacters = (BufferLimit - 3) / 3;

    public static string Compute(SHA256 hash, ref byte[] buffer, string? prefix, string value, TextSpan span)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (span.Start > value.Length || span.Length > value.Length - span.Start)
            throw new ArgumentOutOfRangeException(nameof(span));
        var encoding = Encoding.UTF8;
        // The prefix retains its exact byte count. Use long arithmetic to select
        // streaming without overflowing GetMaxByteCount for a very large slice.
        var capacity = (prefix == null ? 0L : encoding.GetByteCount(prefix) + 1L) + 3L * (span.Length + 1L);
        if (capacity > BufferLimit) return ComputeLarge(hash, ref buffer, prefix, value, span);
        EnsureCapacity(ref buffer, (int)capacity);
        var offset = 0;
        if (prefix != null)
        {
            offset = encoding.GetBytes(prefix, 0, prefix.Length, buffer, 0);
            buffer[offset++] = 0;
        }
        var written = encoding.GetBytes(value, span.Start, span.Length, buffer, offset);
        return CSharpNames.StableId(hash, buffer, offset + written);
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required) return;
        // Old contents are scratch, not accumulated input: never copy them.
        // Geometric growth avoids one new array per slightly larger request.
        buffer = new byte[Math.Min(BufferLimit, Math.Max(required, buffer.Length * 2))];
    }

    private static string ComputeLarge(SHA256 hash, ref byte[] buffer, string? prefix, string value, TextSpan span)
    {
        EnsureCapacity(ref buffer, BufferLimit);
        hash.Initialize();
        try
        {
            if (prefix != null)
            {
                Append(hash, buffer, prefix, 0, prefix.Length);
                buffer[0] = 0;
                hash.TransformBlock(buffer, 0, 1, buffer, 0);
            }
            Append(hash, buffer, value, span.Start, span.Length);
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return CSharpNames.StableIdFromDigest(hash.Hash!);
        }
        finally
        {
            // The same provider also serves subsequent one-shot hashes. Restore
            // it after success or a streaming failure; partial input must not leak.
            hash.Initialize();
        }
    }

    private static void Append(SHA256 hash, byte[] buffer, string value, int start, int length)
    {
        var end = start + length;
        while (start < end)
        {
            var count = Math.Min(ChunkCharacters, end - start);
            // Each slice is encoded independently by the BCL. Never split a valid
            // pair internally; pairs cut by the requested span or prefix boundary
            // must still get the original independent replacement fallback.
            if (count < end - start && char.IsHighSurrogate(value[start + count - 1]) && char.IsLowSurrogate(value[start + count])) count--;
            var written = Encoding.UTF8.GetBytes(value, start, count, buffer, 0);
            hash.TransformBlock(buffer, 0, written, buffer, 0);
            start += count;
        }
    }
}
