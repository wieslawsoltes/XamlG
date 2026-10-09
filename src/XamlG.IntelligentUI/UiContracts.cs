using System.Collections.Immutable;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed record UiLimits(int SourceCharacters = 65536, int Nodes = 512, int Depth = 24,
    int ExpressionCharacters = 2048, int ExpressionNodes = 128, int TextCharacters = 16384,
    int DataBytes = 131072, int StateKeys = 128, int Actions = 32, int Surfaces = 32)
{
    internal void Validate()
    {
        if (SourceCharacters is < 1 or > 1048576 || Nodes is < 1 or > 4096 || Depth is < 1 or > 48 ||
            ExpressionCharacters is < 1 or > 16384 || ExpressionNodes is < 1 or > 1024 ||
            TextCharacters is < 1 or > 131072 || DataBytes is < 2 or > 1048576 ||
            StateKeys is < 1 or > 1024 || Actions is < 0 or > 128 || Surfaces is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(UiLimits));
    }
}

public sealed record UiDiagnostic(string Code, string Message, string? Key = null, int Line = 0, int Column = 0);
public sealed record UiAction(string Id, string Kind, string? Text = null, string? Tool = null, JsonElement? Arguments = null);
public sealed record UiActionIntent(string SurfaceId, long Revision, string Id, string Kind, string? Text, string? Tool, JsonElement? Arguments);
public sealed record UiPublish(string Id, long ExpectedRevision, long Sequence, string Xaml,
    JsonElement? InitialState = null, JsonElement? Data = null, UiAction[]? Actions = null,
    string? FallbackMarkdown = null, bool IsFinal = true);
public sealed record UiRead(string Id);
public sealed record UiStateChange(string Id, long ExpectedRevision, long ExpectedStateRevision, string Key, JsonElement Value);
public sealed record UiActionCall(string Id, long ExpectedRevision, long ExpectedStateRevision, string NodeKey);
public sealed record UiDataChange(string Id, long ExpectedRevision, JsonElement Data);
public sealed record UiRelease(string Id, long ExpectedRevision);

/// <summary>Resolved, inert operations. No CLR type, executable delegate, or markup loader crosses this boundary.</summary>
public sealed record UiElement(string Key, string Type, ImmutableDictionary<string, JsonElement> Properties,
    ImmutableArray<UiElement> Children, string? StateKey = null, string? ActionId = null);
public sealed record UiSnapshot(string Id, long Revision, long StateRevision, long Sequence, bool IsFinal,
    string Xaml, JsonElement State, JsonElement Data, ImmutableArray<UiElement> Roots,
    ImmutableArray<UiAction> Actions, ImmutableArray<UiDiagnostic> Diagnostics, string FallbackMarkdown, string SessionId);
public sealed record UiPresentation(string Format, string Id, long Revision, long StateRevision, string FallbackMarkdown, string SessionId)
{
    public const string FormatName = "xamlg.intelligent-ui/1";
    public static UiPresentation From(UiSnapshot snapshot) => new(FormatName, snapshot.Id, snapshot.Revision, snapshot.StateRevision,
        snapshot.FallbackMarkdown.Length <= 1000 ? snapshot.FallbackMarkdown : snapshot.FallbackMarkdown[..1000] + "\n[Use xamlg_ui_read for complete fallback]", snapshot.SessionId);
    public static bool TryRead(string json, out UiPresentation? presentation)
    {
        presentation = null;
        if (json.Length > 131072 || !json.AsSpan().TrimStart().StartsWith("{")) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("format", out var format) || format.GetString() != FormatName) return false;
            presentation = document.RootElement.Deserialize<UiPresentation>(AutomationJson.Options);
            return presentation is { Id.Length: > 0 and <= 80, SessionId.Length: 32 };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return false; }
    }
}

public sealed class UiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class UiJson
{
    internal static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    internal static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value);
    internal static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
        JsonValueKind.Undefined => null, _ => value
    };
    internal static string Text(object? value) => value switch
    {
        null => "", JsonElement item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText(),
        IFormattable item => item.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    internal static JsonElement Object(JsonElement? value, int maximumBytes, string name)
    {
        var result = value ?? Empty;
        if (result.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(result.GetRawText()) > maximumBytes)
            throw new UiException("invalid_data", name + " must be a bounded JSON object.");
        Check(result, 0);
        return result.Clone();
        static void Check(JsonElement item, int depth)
        {
            if (depth > 32) throw new UiException("invalid_data", "JSON data exceeds 32 levels.");
            if (item.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in item.EnumerateObject())
                {
                    if (!seen.Add(field.Name)) throw new UiException("invalid_data", "Duplicate JSON member.");
                    Check(field.Value, depth + 1);
                }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Check(child, depth + 1);
        }
    }
    internal static void Identifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
            throw new UiException("invalid_identifier", name + " must contain 1–80 ASCII letters, digits, '.', '_' or '-'.");
    }
}
