using System.Buffers;
using System.Text;
namespace XamlG.Syntax;
public static class XmlWhitespace
{
    public static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';
    public static string Normalize(string text, bool preserve) => preserve ? text : Collapse(text, true, true);
    public static string Collapse(string text, bool trimStart, bool trimEnd)
    {
        // Delay output allocation until an actual change. Canonical text (including
        // allowed single boundary spaces) returns its existing immutable string.
        for (var position = 0; position < text.Length;)
        {
            if (!IsWhitespace(text[position])) { position++; continue; }
            var start = position++;
            while (position < text.Length && IsWhitespace(text[position])) position++;
            if (start == 0 && position == text.Length)
                return !trimStart && !trimEnd ? " " : string.Empty;
            if (position - start == 1 && text[start] == ' ' &&
                (start != 0 || !trimStart) && (position != text.Length || !trimEnd)) continue;
            return CollapseChanged(text, start, position, trimStart, trimEnd);
        }
        return text;
    }

    private static string CollapseChanged(string text, int prefixLength, int position, bool trimStart, bool trimEnd)
    {
        // Collapse never expands UTF-16 length: each nonempty whitespace run
        // becomes at most one character. Keep stack use small and bound rental
        // size so a one-off huge literal cannot enlarge the shared pool here.
        const int StackLimit = 256;
        const int PoolLimit = 16 * 1024;
        var longRun = StartsLongRun(text, position);
        // The measured unpooled dense path saved only one builder object while
        // losing throughput. Keep the original loop for that shape; sparse large
        // values can still use bounded scans and bulk copies below.
        if (text.Length > PoolLimit && !longRun)
            return CollapseDenseLarge(text, prefixLength, position, trimStart, trimEnd);
        char[]? rented = null;
        Span<char> output = text.Length <= StackLimit ? stackalloc char[text.Length] :
            text.Length <= PoolLimit ? (rented = ArrayPool<char>.Shared.Rent(text.Length)) :
            new char[text.Length];
        var written = 0;
        try
        {
            text.AsSpan(0, prefixLength).CopyTo(output);
            written = prefixLength;
            // The first changed run was already scanned. Continue from its end,
            // preserving the original pending-space state without rescanning it.
            var pending = true;
            while (longRun)
            {
                // Sixteen checked non-whitespace characters amortize the bulk
                // search/copy dispatch. A short subsequent run falls back once
                // to the scalar loop, never rescanning an unbounded prefix.
                var next = text.AsSpan(position + 16).IndexOfAny(" \t\r\n".AsSpan());
                var length = next < 0 ? text.Length - position : 16 + next;
                if (written != 0 || !trimStart) output[written++] = ' ';
                text.AsSpan(position, length).CopyTo(output.Slice(written));
                written += length;
                position += length;
                if (position == text.Length) return output.Slice(0, written).ToString();
                do { position++; } while (position < text.Length && IsWhitespace(text[position]));
                longRun = StartsLongRun(text, position);
            }
            for (; position < text.Length; position++)
            {
                var character = text[position];
                if (IsWhitespace(character)) { pending = true; continue; }
                if (pending && (written != 0 || !trimStart)) output[written++] = ' ';
                pending = false;
                output[written++] = character;
            }
            if (pending && !trimEnd && (written != 0 || !trimStart)) output[written++] = ' ';
            return output.Slice(0, written).ToString();
        }
        finally
        {
            if (rented != null)
            {
                // Do not leave this document's text in a process-wide pool.
                // Only the initialized prefix was written by this invocation.
                output.Slice(0, written).Clear();
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static bool StartsLongRun(string text, int position)
    {
        if (text.Length - position < 16) return false;
        for (var i = 0; i < 16; i++)
            if (IsWhitespace(text[position + i])) return false;
        return true;
    }

    // Frozen dense suffix from the previous implementation. Selecting it above
    // the rental cap avoids the measured large-dense span-store regression.
    private static string CollapseDenseLarge(string text, int prefixLength, int position, bool trimStart, bool trimEnd)
    {
        var output = new StringBuilder(text.Length);
        output.Append(text, 0, prefixLength);
        var pending = true;
        for (; position < text.Length; position++)
        {
            var character = text[position];
            if (IsWhitespace(character)) { pending = true; continue; }
            if (pending && (output.Length != 0 || !trimStart)) output.Append(' ');
            pending = false;
            output.Append(character);
        }
        if (pending && !trimEnd && (output.Length != 0 || !trimStart)) output.Append(' ');
        return output.ToString();
    }

}
