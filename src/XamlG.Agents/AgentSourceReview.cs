using System.Text;

namespace XamlG.Agents;

public sealed record AgentDiffLine(string Kind, int? BeforeLine, int? AfterLine, string Text);
public sealed record AgentSourceDiff(string Path, IReadOnlyList<AgentDiffLine> Lines, bool Coarse, bool Truncated);

/// <summary>Bounded display diffs and complete source replacement patches. These compare
/// captured project state, including concurrent user edits, without attributing authorship.</summary>
public static class AgentSourceReview
{
    public static AgentSourceDiff Diff(AgentFileChange file, int maximumLines = 1000)
    {
        if (maximumLines is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        if (CountLines(file.Before) > 20000 || CountLines(file.After) > 20000)
        {
            var excerpt = new List<AgentDiffLine>();
            using var oldReader = new StringReader(file.Before ?? "");
            using var newReader = new StringReader(file.After ?? "");
            var line = 0;
            while (excerpt.Count < maximumLines / 2 && oldReader.ReadLine() is { } text)
                excerpt.Add(new("removed", ++line, null, text.Length <= 2000 ? text : text[..2000] + " [excerpt]"));
            line = 0;
            while (excerpt.Count < maximumLines && newReader.ReadLine() is { } text)
                excerpt.Add(new("added", null, ++line, text.Length <= 2000 ? text : text[..2000] + " [excerpt]"));
            return new(file.Path, excerpt, true, true);
        }
        var before = Lines(file.Before); var after = Lines(file.After);
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        var oldCount = before.Length - prefix - suffix; var newCount = after.Length - prefix - suffix;
        var coarse = (long)(oldCount + 1) * (newCount + 1) > 250000;
        var result = new List<AgentDiffLine>(); var truncated = false; var characters = 0;
        void Add(string kind, int? oldLine, int? newLine, string text)
        {
            if (result.Count >= maximumLines || characters >= 1_000_000) { truncated = true; return; }
            if (text.Length > 20000) { text = text[..20000] + " [line excerpt]"; truncated = true; }
            result.Add(new(kind, oldLine, newLine, text));
            characters += text.Length;
        }
        for (var i = Math.Max(0, prefix - 3); i < prefix; i++) Add("context", i + 1, i + 1, before[i]);
        if (coarse)
        {
            for (var i = 0; i < oldCount && result.Count < maximumLines; i++) Add("removed", prefix + i + 1, null, before[prefix + i]);
            for (var i = 0; i < newCount && result.Count < maximumLines; i++) Add("added", null, prefix + i + 1, after[prefix + i]);
            truncated |= oldCount + newCount + Math.Min(3, prefix) > maximumLines;
        }
        else
        {
            var matrix = new int[oldCount + 1, newCount + 1];
            for (var i = oldCount - 1; i >= 0; i--)
                for (var j = newCount - 1; j >= 0; j--)
                    matrix[i, j] = before[prefix + i] == after[prefix + j] ? matrix[i + 1, j + 1] + 1 : Math.Max(matrix[i + 1, j], matrix[i, j + 1]);
            var old = 0; var next = 0;
            while (old < oldCount || next < newCount)
            {
                if (result.Count >= maximumLines) { truncated = true; break; }
                if (old < oldCount && next < newCount && before[prefix + old] == after[prefix + next])
                { Add("context", prefix + old + 1, prefix + next + 1, before[prefix + old]); old++; next++; }
                else if (old < oldCount && (next == newCount || matrix[old + 1, next] >= matrix[old, next + 1]))
                { Add("removed", prefix + old + 1, null, before[prefix + old]); old++; }
                else { Add("added", null, prefix + next + 1, after[prefix + next]); next++; }
            }
        }
        for (var i = 0; i < Math.Min(3, suffix); i++) Add("context", before.Length - suffix + i + 1, after.Length - suffix + i + 1, after[after.Length - suffix + i]);
        return new(file.Path, result, coarse, truncated);
    }

    public static string Patch(AgentChangeReview review)
    {
        var patch = new StringBuilder();
        foreach (var file in review.Files)
        {
            var before = CountLines(file.Before); var after = CountLines(file.After);
            patch.Append("--- ").AppendLine(file.Before == null ? "/dev/null" : Quote("a/" + file.Path));
            patch.Append("+++ ").AppendLine(file.After == null ? "/dev/null" : Quote("b/" + file.Path));
            patch.Append("@@ -").Append(before == 0 ? 0 : 1).Append(',').Append(before)
                .Append(" +").Append(after == 0 ? 0 : 1).Append(',').Append(after).AppendLine(" @@");
            Append(file.Before, '-'); Append(file.After, '+');
            if (patch.Length > 8_000_000) throw new InvalidOperationException("The review patch exceeds 8 MB. Export individual project documents.");
        }
        return patch.ToString();

        void Append(string? source, char prefix)
        {
            for (var offset = 0; source != null && offset < source.Length;)
            {
                var end = source.IndexOf('\n', offset); if (end < 0) end = source.Length;
                patch.Append(prefix).Append(source, offset, end - offset).Append('\n'); offset = end + 1;
                if (patch.Length > 8_000_000) throw new InvalidOperationException("The review patch exceeds 8 MB. Export individual project documents.");
            }
            if (source is { Length: > 0 } && !source.EndsWith('\n')) patch.Append("\\ No newline at end of file\n");
        }
    }

    private static string Quote(string path) => "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    private static string[] Lines(string? text) => string.IsNullOrEmpty(text) ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
    private static int CountLines(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
}
