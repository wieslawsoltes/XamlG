using System.Text;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace XamlG.Agents;

public sealed record AgentDiffLine(string Kind, int? BeforeLine, int? AfterLine, string Text,
    string? BlockId = null, string? LineEnding = null);
public sealed record AgentDiffBlock(string Id, int FirstRow, int LastRow, int BeforeStart, int BeforeLength,
    int AfterStart, int AfterLength, int RemovedLines, int AddedLines);
public sealed record AgentSourceDiff(string Path, IReadOnlyList<AgentDiffLine> Lines, bool Coarse, bool Truncated)
{
    public IReadOnlyList<AgentDiffBlock> Blocks { get; init; } = [];
    public int FirstRow { get; init; }
    public int TotalRows { get; init; }
    public bool SelectiveRestoreAvailable { get; init; }
}
public sealed record AgentBlockPreview(AgentDiffBlock Block, string Before, string After, bool Truncated, bool Coarse);

/// <summary>Bounded display diffs, exact source blocks and complete replacement patches.
/// Captures include concurrent user edits, without attributing authorship. UTF-16 offsets
/// preserve original line endings and missing-final-newline state; no fuzzy matching.</summary>
public static class AgentSourceReview
{
    internal static string ContentIdentity(AgentFileChange file)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var text in new[] { file.Before, file.After })
        {
            BinaryPrimitives.WriteInt32LittleEndian(length, text?.Length ?? -1); hash.AppendData(length);
            if (text != null) hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static AgentSourceDiff Diff(AgentFileChange file, int maximumLines = 1000, int firstRow = 0)
    {
        if (maximumLines is < 1 or > 10000 || firstRow is < 0 or > 40000) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        var analysis = Analyze(file);
        if (analysis.Oversized)
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
        var total = analysis.Rows.Count;
        if (firstRow > total) throw new ArgumentOutOfRangeException(nameof(firstRow));
        var result = new List<AgentDiffLine>(); var characters = 0; var truncated = firstRow != 0;
        foreach (var row in analysis.Rows.Skip(firstRow).Take(maximumLines))
        {
            if (characters >= 1_000_000) { truncated = true; break; }
            var ending = row.Text.EndsWith("\r\n", StringComparison.Ordinal) ? "CRLF" : row.Text.EndsWith('\n') ? "LF" : "none";
            var text = ending == "CRLF" ? row.Text[..^2] : ending == "LF" ? row.Text[..^1] : row.Text;
            if (text.Length > 20000) { text = text[..20000] + " [line excerpt]"; truncated = true; }
            result.Add(new(row.Kind, row.BeforeLine, row.AfterLine, text, row.BlockId, ending)); characters += text.Length;
        }
        return new(file.Path, result, analysis.Coarse, truncated || firstRow + result.Count < total)
        {
            Blocks = analysis.Blocks, FirstRow = firstRow, TotalRows = total,
            SelectiveRestoreAvailable = file.Before != null && file.After != null && analysis.Blocks.Count != 0
        };
    }

    public static AgentBlockPreview PreviewBlock(AgentFileChange file, string blockId)
    {
        var analysis = Analyze(file); var block = FindBlock(file, analysis, blockId);
        return new(block, file.Before!.Substring(block.BeforeStart, Math.Min(block.BeforeLength, 20000)),
            file.After!.Substring(block.AfterStart, Math.Min(block.AfterLength, 20000)),
            block.BeforeLength > 20000 || block.AfterLength > 20000, analysis.Coarse);
    }

    /// <summary>Returns a full-source transaction. Its After remains the exact captured
    /// current text, so an embedding workspace must reject any intervening edit.</summary>
    public static AgentFileChange RestoreBlock(AgentFileChange file, string blockId)
    {
        var block = FindBlock(file, Analyze(file), blockId);
        var restored = string.Concat(file.After.AsSpan(0, block.AfterStart), file.Before.AsSpan(block.BeforeStart, block.BeforeLength),
            file.After.AsSpan(block.AfterStart + block.AfterLength));
        return new(file.Path, restored, file.After);
    }
    private static AgentDiffBlock FindBlock(AgentFileChange file, Analysis analysis, string id)
    {
        if (file.Before == null || file.After == null || analysis.Oversized)
            throw new InvalidOperationException("Selective restore requires a complete comparison of an existing document within the interactive line limit.");
        return analysis.Blocks.SingleOrDefault(block => block.Id == id) ?? throw new ArgumentException("Select a block from the current review.");
    }
    private sealed record Row(string Kind, int? BeforeLine, int? AfterLine, string Text, string? BlockId);
    private sealed record Analysis(List<Row> Rows, List<AgentDiffBlock> Blocks, bool Coarse, bool Oversized = false);
    private static Analysis Analyze(AgentFileChange file)
    {
        if ((long)(file.Before?.Length ?? 0) + (file.After?.Length ?? 0) > 16_000_000)
            throw new ArgumentException("The source comparison exceeds 16 million characters.");
        if (CountLines(file.Before) > 20000 || CountLines(file.After) > 20000) return new([], [], true, true);
        var before = ExactLines(file.Before); var after = ExactLines(file.After);
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        var oldCount = before.Length - prefix - suffix; var newCount = after.Length - prefix - suffix;
        var coarse = (long)(oldCount + 1) * (newCount + 1) > 250000;
        var rows = new List<Row>(); var blocks = new List<AgentDiffBlock>();
        var oldLine = 0; var newLine = 0; var oldOffset = 0; var newOffset = 0;
        AgentDiffBlock? active = null;
        var trim = Math.Max(0, prefix - 3);
        void FinishBlock() { if (active != null) { blocks.Add(active); active = null; } }
        void Add(string kind, string text)
        {
            if (kind == "context") FinishBlock();
            else
            {
                active ??= new("change-" + (blocks.Count + 1), rows.Count, rows.Count, oldOffset, 0, newOffset, 0, 0, 0);
                active = active with { LastRow = rows.Count,
                    BeforeLength = active.BeforeLength + (kind == "removed" ? text.Length : 0),
                    AfterLength = active.AfterLength + (kind == "added" ? text.Length : 0),
                    RemovedLines = active.RemovedLines + (kind == "removed" ? 1 : 0), AddedLines = active.AddedLines + (kind == "added" ? 1 : 0) };
            }
            var old = kind == "added" ? (int?)null : ++oldLine;
            var next = kind == "removed" ? (int?)null : ++newLine;
            if (kind != "added") oldOffset += text.Length;
            if (kind != "removed") newOffset += text.Length;
            if (kind != "context" || old > trim && old <= before.Length - suffix + 3)
                rows.Add(new(kind, old, next, text, active?.Id));
        }
        for (var i = 0; i < prefix; i++) Add("context", before[i]);
        if (coarse)
        {
            for (var i = 0; i < oldCount; i++) Add("removed", before[prefix + i]);
            for (var i = 0; i < newCount; i++) Add("added", after[prefix + i]);
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
                if (old < oldCount && next < newCount && before[prefix + old] == after[prefix + next])
                { Add("context", before[prefix + old]); old++; next++; }
                else if (old < oldCount && (next == newCount || matrix[old + 1, next] >= matrix[old, next + 1])) Add("removed", before[prefix + old++]);
                else Add("added", after[prefix + next++]);
            }
        }
        for (var i = before.Length - suffix; i < before.Length; i++) Add("context", before[i]);
        FinishBlock(); return new(rows, blocks, coarse);
    }
    private static string[] ExactLines(string? source)
    {
        if (string.IsNullOrEmpty(source)) return [];
        var lines = new List<string>();
        for (var offset = 0; offset < source.Length;)
        {
            var end = source.IndexOf('\n', offset); end = end < 0 ? source.Length : end + 1;
            lines.Add(source[offset..end]); offset = end;
        }
        return lines.ToArray();
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
    private static int CountLines(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
}
