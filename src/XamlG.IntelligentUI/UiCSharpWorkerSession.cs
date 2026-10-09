using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

/// <summary>Stateful worker-side execution protocol. The embedding host MUST place this object
/// inside a terminable isolated worker/process; this class is not an in-process security sandbox.
/// It exposes no application tools, external actions, arbitrary method invocation or credentials.</summary>
public sealed class UiCSharpWorkerSession : IDisposable
{
    private const string Principal = "approved-execution";
    private readonly UiCSharpExpressionCompiler _compiler;
    private readonly UiSessionStore _store;
    private bool _published, _disposed;
    public UiCSharpWorkerSession(IEnumerable<MetadataReference> references, Func<UiCSharpExecutionRequest, bool> approve)
    {
        ArgumentNullException.ThrowIfNull(references); ArgumentNullException.ThrowIfNull(approve);
        _compiler = new(references, approve, maximumCompilations: 64);
        _store = new(new UiCompiler(expressionCompiler: _compiler));
    }
    /// <summary>One bounded JSON command. The worker supervisor, not evaluated code, owns deadlines.</summary>
    public string Dispatch(string method, string json)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(method) || method.Length > 32 || json == null || json.Length > 1048576)
            throw new UiException("execution_limit", "The worker request exceeds its bound.");
        object result = method switch
        {
            "publish" => Publish(Read<UiPublish>(json)),
            "read" => _store.Read(Read<UiRead>(json).Id, Principal),
            "state" => _store.ChangeState(Read<UiStateChange>(json), Principal),
            "state_action" => _store.ApplyStateAction(Read<UiActionCall>(json), Principal),
            "form_touch" => _store.TouchForm(Read<UiFormCall>(json), Principal),
            "form_submit" => _store.SubmitForm(Read<UiFormCall>(json), Principal),
            "form_reset" => _store.ResetForm(Read<UiFormCall>(json), Principal),
            // No registration, arbitrary tool, persistence, clipboard, URL or inference command exists.
            _ => throw new UiException("execution_command", "The worker command is not permitted.")
        };
        var output = JsonSerializer.Serialize(result, AutomationJson.Options);
        if (Encoding.UTF8.GetByteCount(output) > 2097152)
            throw new UiException("execution_limit", "The worker result exceeds its bound.");
        return output;
    }
    private UiSnapshot Publish(UiPublish request)
    {
        if (_published) throw new UiException("execution_used", "Create a new approved worker for another declaration.");
        request = UiCSharpDeclaration.Normalize(request);
        // Mark before compilation. A failed execution does not authorize replacement source.
        _published = true;
        return _store.Publish(request with { ExpectedRevision = 0, Sequence = 1 }, Principal);
    }
    private static T Read<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, AutomationJson.Options) ?? throw new UiException("invalid_data", "A worker command object is required."); }
        catch (JsonException error) { throw new UiException("invalid_data", error.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _store.Clear(); _compiler.Dispose();
    }
}
