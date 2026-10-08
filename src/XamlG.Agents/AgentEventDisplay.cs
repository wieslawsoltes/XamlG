using System.Text.Json;

namespace XamlG.Agents;

// A bounded view of public events. Provider history and transcript exports retain the original result.
internal sealed record AgentEventDisplay(long Sequence, DateTimeOffset Time, string TaskId, string Kind,
    string Text, string? ToolCallId, string? ToolName, JsonElement? ResultPreview)
{
    public static AgentEventDisplay Create(AgentEvent item, int limit)
    {
        JsonElement? preview = null;
        if (item.Kind == "tool_completed" && item.Text.Length > limit)
        {
            try
            {
                using var document = JsonDocument.Parse(item.Text);
                var budget = new PreviewBudget();
                preview = JsonSerializer.SerializeToElement(budget.Visit(document.RootElement, 4));
            }
            catch (JsonException) { }
        }
        return new(item.Sequence, item.Time, item.TaskId, item.Kind,
            item.Text.Length > limit ? item.Text[..limit] + "\n[see transcript export]" : item.Text,
            item.ToolCallId, item.ToolName, preview);
    }

    private sealed class PreviewBudget
    {
        private int _nodes = 96, _characters = 2048;
        private string Clip(string value, int limit)
        {
            var count = Math.Min(value.Length, Math.Min(limit, _characters));
            _characters -= count;
            return count == value.Length ? value : value[..count] + "…";
        }
        public object? Visit(JsonElement value, int depth)
        {
            _nodes--;
            if (value.ValueKind == JsonValueKind.Object)
            {
                var fields = value.EnumerateObject().ToArray();
                var result = new Dictionary<string, object?>();
                var taken = 0;
                foreach (var field in fields)
                {
                    if (depth == 0 || taken == 12 || _nodes <= 0 || _characters <= 0) break;
                    var key = Clip(field.Name, 64);
                    if (result.ContainsKey(key)) key += $" ({taken})";
                    result[key] = Visit(field.Value, depth - 1); taken++;
                }
                if (taken < fields.Length) result["$moreFields"] = fields.Length - taken;
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var result = new List<object?>();
                foreach (var item in value.EnumerateArray())
                {
                    if (depth == 0 || result.Count == 8 || _nodes <= 0 || _characters <= 0) break;
                    result.Add(Visit(item, depth - 1));
                }
                if (result.Count < value.GetArrayLength()) result.Add(new Dictionary<string, object?> { ["$moreItems"] = value.GetArrayLength() - result.Count });
                return result;
            }
            if (value.ValueKind == JsonValueKind.String) return Clip(value.GetString() ?? "", 160);
            return value.GetRawText().Length > 160 ? Clip(value.GetRawText(), 160) : value.Clone();
        }
    }
}
