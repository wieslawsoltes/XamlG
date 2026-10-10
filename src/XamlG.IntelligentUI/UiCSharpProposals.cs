using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed record UiCSharpProposal(string Id, string Sha256, UiPublish Request, string Status, JsonElement? Result = null, string? Error = null);
public sealed record UiCSharpProposalRead(string Id);

/// <summary>Non-executing proposal mailbox. Agents may propose code and read their result;
/// only trusted local UI can record an owner-approved run. No approval or delegate is persisted.</summary>
public sealed class UiCSharpProposals(UiSessionStore workspace)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Owner, UiCSharpProposal Proposal)> _entries = new(StringComparer.Ordinal);
    private long _epoch = workspace.DataLifetime;
    public event Action? Changed;
    public void Register(AutomationCatalog catalog)
    {
        catalog.Add<UiPublish, UiCSharpProposal>("xamlg_ui_csharp_propose", "Propose a full Roslyn C# intelligent UI for explicit local owner review. Does NOT compile, execute, request inference, grant permissions or open a frame. The owner reviews exact source, data and local state-action code, then starts a disposable isolated frame. Use ordinary JsonElement C# APIs for state/data. Only declared state actions are allowed; no external tool, message, link or clipboard action authority is available.", AutomationScope.Agent, AutomationEffect.Edit,
            (args, context) => { context.CancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Propose(args, Owner(context))); });
        catalog.Add<UiCSharpProposalRead, UiCSharpProposal>("xamlg_ui_csharp_status", "Read the status and bounded result of this caller's full-C# UI proposal. Pending means the owner has not run it. Never describe a proposal as executed before the status says completed.", AutomationScope.Agent, AutomationEffect.Read,
            (args, context) =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                lock (_gate) { Synchronize(); return ValueTask.FromResult(Get(args.Id, Owner(context)).Proposal); }
            });
    }
    private static string Owner(AutomationCallContext context) => string.IsNullOrWhiteSpace(context.PrincipalId) || context.PrincipalId.Length > 200
        ? throw new AutomationException("invalid_principal", "A transport-derived principal is required.") : context.PrincipalId;
    private UiCSharpProposal Propose(UiPublish request, string owner)
    {
        try
        {
            var normalized = UiCSharpDeclaration.Normalize(request, workspace.Compiler.Limits);
            var json = JsonSerializer.Serialize(normalized, AutomationJson.Options);
            var proposal = new UiCSharpProposal(Guid.NewGuid().ToString("N"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), normalized, "pending");
            lock (_gate)
            {
                Synchronize();
                if (_entries.Count >= 8) throw new UiException("proposal_limit", "The owner must clear finished proposals before adding another.");
                _entries.Add(proposal.Id, (owner, proposal));
            }
            Notify(); return proposal;
        }
        catch (UiException error) { throw new AutomationException(error.Code, error.Message); }
    }
    /// <summary>Trusted owner-only inventory. Do not expose across principals as a tool.</summary>
    public IReadOnlyList<UiCSharpProposal> SnapshotLocal()
    { lock (_gate) { Synchronize(); return _entries.Values.Select(entry => entry.Proposal).ToArray(); } }
    public void CompleteLocal(string id, string approvedSha256, JsonElement? result, string? error)
    {
        if (result is { } value) UiDataStore.ValidateJson(value, 131072);
        if (error?.Length > 4096) error = error[..4096];
        lock (_gate)
        {
            Synchronize();
            if (!_entries.TryGetValue(id, out var entry) || entry.Proposal.Sha256 != approvedSha256) throw new UiException("unknown_proposal", "The reviewed proposal was retired.");
            _entries[id] = (entry.Owner, entry.Proposal with { Status = error == null ? "completed" : "failed", Result = result?.Clone(), Error = error });
        }
        Notify();
    }
    public void ClearLocal() { lock (_gate) _entries.Clear(); Notify(); }
    private (string Owner, UiCSharpProposal Proposal) Get(string id, string owner)
        => _entries.TryGetValue(id, out var entry) && entry.Owner == owner ? entry : throw new AutomationException("unknown_proposal", "This proposal is unavailable.");
    private void Synchronize()
    { var epoch = workspace.DataLifetime; if (epoch != _epoch) { _entries.Clear(); _epoch = epoch; } }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
