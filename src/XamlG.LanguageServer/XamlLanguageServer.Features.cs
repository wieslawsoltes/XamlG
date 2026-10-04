using System.Text.Json;
using XamlG.LanguageServer.Diagnostics;

namespace XamlG.LanguageServer;

public sealed partial class XamlLanguageServer
{
    private LspClientFeatures _clientFeatures = new(false, false, false, false, false);
    private readonly LspDiagnosticCache _diagnosticReports = new();
    private LspDiagnosticRefreshQueue? _diagnosticRefresh;

    private object InitializeFeatures(JsonElement parameters)
    {
        _clientFeatures = LspClientFeatures.Read(parameters);
        _versionedEdits = _clientFeatures.VersionedEdits;
        if (_clientFeatures.PullDiagnostics && _clientFeatures.DiagnosticRefresh)
            _diagnosticRefresh = new((message, token) => _connection.WriteAsync(message, token), _lifetime.Token,
                report: error => _ = _log.WriteLineAsync("diagnostic refresh: " + error.Message));
        return new { capabilities = LspServerCapabilities.Create(_clientFeatures), serverInfo = new { name = "XamlG", version = "0.1.0-alpha.1" } };
    }
    private object? HandleWorkspaceRequest(string method, JsonElement parameters, LspWorkspaceAnalysis workspace,
        LspDocumentSetSnapshot buffers, CancellationToken token) => method switch
    {
        LspDiagnosticMethods.Document or LspDiagnosticMethods.Workspace => new LspPullDiagnosticRequests(_diagnosticReports, _clientFeatures.RelatedDiagnostics)
            .Handle(method, parameters, workspace, buffers, token),
        LspFileRenameRequests.Method => new LspFileRenameRequests(workspace.Compiler, _versionedEdits).Handle(parameters, buffers, workspace.Documents, token),
        _ => new LspRequestHandler(workspace.Compiler, _documents, _semanticTokens, _versionedEdits).Handle(method, parameters, token, buffers, workspace.Documents)
    };
    private bool AcceptClientResponse(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id) || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
            !(message.TryGetProperty("result", out _) ^ message.TryGetProperty("error", out _))) return false;
        _diagnosticRefresh?.AcceptResponse(message);
        // Responses, including late/unknown responses, are never answered with another response.
        return true;
    }
}
