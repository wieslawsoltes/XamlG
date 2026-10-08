using System.Text.Json;
using System.Text.Json.Serialization;

namespace XamlG.Automation;

/// <summary>Explicit bounded media result. Adapters send images as images, never as base64 text context.</summary>
public sealed record AutomationImage(string MimeType, string Data, int Width, int Height);
public sealed record AutomationMediaResult([property: JsonPropertyName("$xamlgMedia")] int MediaVersion, JsonElement Metadata, IReadOnlyList<AutomationImage> Images);
public static class AutomationMedia
{
    public static JsonElement Image(object metadata, byte[] png, int width, int height) =>
        AutomationJson.Element(new AutomationMediaResult(1, AutomationJson.Element(metadata), [new("image/png", Convert.ToBase64String(png), width, height)]));

    public static bool TryRead(string text, out AutomationMediaResult media)
    {
        media = null!;
        if (!text.Contains("\"$xamlgMedia\"", StringComparison.Ordinal)) return false;
        try { using var parsed = JsonDocument.Parse(text); return TryRead(parsed.RootElement, out media); }
        catch (JsonException) { return false; }
    }
    public static bool TryRead(JsonElement result, out AutomationMediaResult media)
    {
        media = null!;
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("$xamlgMedia", out var version)) return false;
        if (!version.TryGetInt32(out var number) || number != 1) throw new ArgumentException("Unsupported automation media version.");
        var value = result.Deserialize<AutomationMediaResult>(AutomationJson.Options) ?? throw new ArgumentException("Invalid media result.");
        if (value.Images.Count is < 1 or > 4 || value.Images.Sum(image => (long)image.Data.Length) > 8_388_608)
            throw new ArgumentException("Media result exceeds its image budget.");
        foreach (var image in value.Images)
        {
            if (image.MimeType is not ("image/png" or "image/jpeg") || image.Width is < 1 or > 4096 || image.Height is < 1 or > 4096 || image.Data.Length > 2_796_204)
                throw new ArgumentException("Invalid automation image.");
            try { _ = Convert.FromBase64String(image.Data); }
            catch (FormatException) { throw new ArgumentException("Invalid image encoding."); }
        }
        media = value; return true;
    }
}
