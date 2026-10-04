using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace XamlG.LanguageServer.Diagnostics;

public sealed record LspWorkspaceDocumentDiagnosticReport(string Uri, int? Version, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<LspDiagnosticItem>? Items { get; init; }
}
