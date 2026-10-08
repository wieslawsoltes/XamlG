using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace XamlG.Playground.Components;

/// <summary>A bounded text-only subset. Model HTML, images and embeds remain text;
/// only absolute HTTP(S) links without credentials become navigable anchors.</summary>
public partial class AgentMarkdown
{
    [Parameter] public string Text { get; set; } = "";
    [Parameter] public EventCallback<string> CopyRequested { get; set; }
    private string? _parsedText;
    private bool _truncated;
    private readonly List<Block> _blocks = [];
    private static readonly Regex InlinePattern = new(@"`[^`\n]+`|\*\*[^*\n]+\*\*|\[[^\[\]\n]+\]\([^\s()[\]]+\)", RegexOptions.NonBacktracking);
    private static readonly Regex ListPattern = new(@"^\s*(?<mark>[-*]|[0-9]+[.)])\s+", RegexOptions.NonBacktracking);
    protected override void OnParametersSet()
    {
        if (_parsedText == Text) return;
        _parsedText = Text; _blocks.Clear(); _truncated = Text.Length > 262144;
        var text = _truncated ? Text[..262144] : Text;
        var lines = text.Split('\n'); var budget = 500;
        for (var i = 0; i < lines.Length;)
        {
            if (--budget < 0) { _blocks.Add(new("plain", string.Join('\n', lines, i, lines.Length - i))); break; }
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            if (Fence(lines[i], out var mark, out var count, out var language))
            {
                var first = ++i;
                while (i < lines.Length && !ClosesFence(lines[i], mark, count)) i++;
                var value = string.Join('\n', lines, first, i - first);
                _blocks.Add(new("code", value, language.Length == 0 ? "Code" : language));
                if (i < lines.Length) i++;
                continue;
            }
            var heading = HeadingLength(lines[i]);
            if (heading != 0) { _blocks.Add(new("heading", Parts: Parts(lines[i++][heading..], ref budget))); continue; }
            var list = ListPattern.Match(lines[i]);
            if (list.Success)
            {
                var ordered = char.IsAsciiDigit(list.Groups["mark"].Value[0]); var items = new List<Part[]>();
                while (i < lines.Length && (list = ListPattern.Match(lines[i])).Success &&
                    char.IsAsciiDigit(list.Groups["mark"].Value[0]) == ordered && budget-- > 0)
                    items.Add(Parts(lines[i++][list.Length..], ref budget));
                _blocks.Add(new(ordered ? "ordered" : "unordered", Items: items)); continue;
            }
            var start = i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && HeadingLength(lines[i]) == 0 &&
                !ListPattern.IsMatch(lines[i]) && !Fence(lines[i], out _, out _, out _)) i++;
            _blocks.Add(new("paragraph", Parts: Parts(string.Join('\n', lines, start, i - start), ref budget)));
        }
    }
    private static Part[] Parts(string text, ref int budget)
    {
        var parts = new List<Part>(); var end = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (--budget < 0) break;
            if (match.Index > end) parts.Add(new("text", text[end..match.Index]));
            var value = match.Value;
            if (value[0] == '`') parts.Add(new("code", value[1..^1]));
            else if (value.StartsWith("**", StringComparison.Ordinal)) parts.Add(new("strong", value[2..^2]));
            else
            {
                var split = value.IndexOf("](", StringComparison.Ordinal); var href = value[(split + 2)..^1];
                var safe = (match.Index == 0 || text[match.Index - 1] != '!') && Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
                parts.Add(safe ? new("link", value[1..split], href) : new("text", value));
            }
            end = match.Index + match.Length;
        }
        if (end < text.Length) parts.Add(new("text", text[end..]));
        return parts.ToArray();
    }
    private static int HeadingLength(string text)
    {
        var i = 0; while (i < text.Length && text[i] == '#') i++;
        return i is >= 1 and <= 3 && i < text.Length && char.IsWhiteSpace(text[i]) ? i + 1 : 0;
    }
    private static bool Fence(string text, out char mark, out int count, out string language)
    {
        var trimmed = text.AsSpan().TrimStart(); count = 0; mark = trimmed.Length == 0 ? '\0' : trimmed[0]; language = "";
        if (mark is not ('`' or '~')) return false;
        while (count < trimmed.Length && trimmed[count] == mark) count++;
        if (count < 3) return false;
        var suffix = trimmed[count..].Trim(); language = suffix[..Math.Min(suffix.Length, 40)].ToString(); return true;
    }
    private static bool ClosesFence(string text, char mark, int length)
    {
        var trimmed = text.AsSpan().Trim(); var i = 0;
        while (i < trimmed.Length && trimmed[i] == mark) i++;
        return i >= length && i == trimmed.Length;
    }
    private static RenderFragment Inline(Part[]? parts) => builder =>
    {
        foreach (var part in parts ?? [])
        {
            if (part.Kind == "text") { builder.AddContent(0, part.Text); continue; }
            builder.OpenElement(1, part.Kind == "link" ? "a" : part.Kind);
            if (part.Kind == "link")
            { builder.AddAttribute(2, "href", part.Href); builder.AddAttribute(3, "target", "_blank"); builder.AddAttribute(4, "rel", "noopener noreferrer"); }
            builder.AddContent(5, part.Text); builder.CloseElement();
        }
    };
    private sealed record Part(string Kind, string Text, string? Href = null);
    private sealed record Block(string Kind, string Text = "", string Language = "", Part[]? Parts = null, List<Part[]>? Items = null)
    {
        public List<Part[]> Items { get; init; } = Items ?? [];
    }
}
