using System.Text;
using System.Text.Json;

namespace XamlG.Automation;

public sealed partial class AutomationCatalog
{
    /// <summary>Attach inert transport metadata without changing tool authority or its handler.</summary>
    public void SetMetadata(string name, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(metadata.GetRawText()) > 8192)
            throw new ArgumentException("Tool metadata must be an object of at most 8192 UTF-8 bytes.", nameof(metadata));
        var copy = metadata.Clone();
        lock (_gate)
        {
            var entry = _tools.TryGetValue(name, out var found) ? found : throw new KeyNotFoundException("Unknown tool.");
            _tools[name] = entry with { Tool = entry.Tool with { Metadata = copy } };
        }
        NotifyCatalogChanged();
    }
}
