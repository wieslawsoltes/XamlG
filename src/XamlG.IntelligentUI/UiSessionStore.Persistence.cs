using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using XamlG.Automation;

namespace XamlG.IntelligentUI;

public sealed record UiArchivedSurface(string Owner, string Id, string SessionId, long Revision, long StateRevision,
    long Sequence, bool IsFinal, string Xaml, JsonElement State, JsonElement Data, UiAction[] Actions, string? FallbackMarkdown);
public sealed record UiWorkspaceArchive(string Format, int Version, string WorkspaceId, string ExpressionLanguage,
    long Generation, long Revision, UiArchivedSurface[] Surfaces)
{
    public const string FormatName = "xamlg.ui-workspace";
    public const int MaximumBytes = 16 * 1024 * 1024;
}

public sealed partial class UiSessionStore
{
    /// <summary>Trusted owner persistence. This contains principal identities and user data; never
    /// expose it as a remote tool. Store it only after the user opts in to private workspace persistence.</summary>
    public string CaptureArchive(string workspaceId)
    {
        ValidateWorkspaceId(workspaceId); UiWorkspaceArchive archive;
        lock (_gate)
        {
            archive = new(UiWorkspaceArchive.FormatName, 1, workspaceId, Compiler.ExpressionLanguage, _generation, _revision,
                _entries.Values.OrderBy(entry => entry.Snapshot.Id, StringComparer.Ordinal).Select(entry =>
                {
                    var s = entry.Snapshot;
                    return new UiArchivedSurface(entry.Principal, s.Id, s.SessionId, s.Revision, s.StateRevision, s.Sequence, s.IsFinal, s.Xaml, s.State, s.Data, s.Actions.ToArray(), entry.Markdown);
                }).ToArray());
        }
        var json = JsonSerializer.Serialize(archive, AutomationJson.Options);
        if (Encoding.UTF8.GetByteCount(json) > UiWorkspaceArchive.MaximumBytes) throw new UiException("archive_limit", "UI workspace archive exceeds 16 MiB.");
        return json;
    }
    /// <summary>Restore only the trusted private archive of the requested workspace into an empty
    /// store. Every declaration is recompiled and every value revalidated before one atomic publication.
    /// Resource handles, tool grants, leases, and executing delegates are never restored.</summary>
    public void RestoreArchive(string json, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(json); ValidateWorkspaceId(workspaceId);
        if (Encoding.UTF8.GetByteCount(json) > UiWorkspaceArchive.MaximumBytes) throw new UiException("archive_limit", "UI workspace archive exceeds 16 MiB.");
        UiWorkspaceArchive archive;
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 48 });
            // Reject duplicate object keys before deserializing security-sensitive owner/session fields.
            _ = UiJson.Object(document.RootElement, UiWorkspaceArchive.MaximumBytes, "Archive");
            archive = document.RootElement.Deserialize<UiWorkspaceArchive>(AutomationJson.Options) ?? throw new JsonException("Missing archive.");
        }
        catch (JsonException error) { throw new UiException("invalid_archive", error.Message); }
        if (archive.Format != UiWorkspaceArchive.FormatName || archive.Version != 1 || archive.WorkspaceId != workspaceId || archive.ExpressionLanguage != Compiler.ExpressionLanguage || archive.Generation < 0 || archive.Revision < 0 || archive.Surfaces == null || archive.Surfaces.Length > Compiler.Limits.Surfaces)
            throw new UiException("invalid_archive", "Archive version, workspace, expression backend or bounds do not match this host.");
        long generation, lifetime;
        lock (_gate)
        {
            if (_entries.Count != 0) throw new UiException("revision_conflict", "Restore requires an empty UI workspace.");
            generation = _generation; lifetime = _lifetime;
        }
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var saved in archive.Surfaces)
        {
            if (saved == null) throw new UiException("invalid_archive", "Null surface.");
            Principal(saved.Owner); UiJson.Identifier(saved.Id, "Surface ID");
            if (saved.SessionId == null || saved.SessionId.Length != 32 || !saved.SessionId.All(char.IsAsciiHexDigit) || !identities.Add(saved.SessionId) || saved.Revision < 1 || saved.Revision > archive.Revision || saved.StateRevision < 0 || saved.Sequence < 1 || saved.Xaml == null || saved.Actions == null || saved.FallbackMarkdown?.Length > Compiler.Limits.TextCharacters)
                throw new UiException("invalid_archive", "Invalid archived identity or revision.");
            var state = ValidateState(saved.State); var data = UiJson.Object(saved.Data, Compiler.Limits.DataBytes, "Data"); var actions = ValidateActions(saved.Actions);
            var compilation = Compiler.Compile(saved.Xaml, saved.IsFinal);
            if (!compilation.Success) throw new UiException("invalid_archive", "Archived XAML no longer compiles: " + saved.Id);
            var roots = compilation.Template!.Render(state, data); ValidateActionReferences(roots, actions);
            var snapshot = new UiSnapshot(saved.Id, saved.Revision, saved.StateRevision, saved.Sequence, saved.IsFinal, saved.Xaml, state, data, roots, actions, compilation.Diagnostics, Fallback(roots, saved.FallbackMarkdown), saved.SessionId);
            if (!entries.TryAdd(saved.Id, new(saved.Owner, compilation.Template, snapshot, saved.FallbackMarkdown))) throw new UiException("invalid_archive", "Duplicate surface identity.");
        }
        lock (_gate)
        {
            if (_entries.Count != 0 || _generation != generation || _lifetime != lifetime) throw new UiException("workspace_changed", "UI workspace changed during archive validation.");
            foreach (var entry in entries) _entries.Add(entry.Key, entry.Value);
            _revision = Math.Max(_revision, archive.Revision); _generation = checked(Math.Max(_generation, archive.Generation) + 1); _lifetime = checked(_lifetime + 1);
        }
        foreach (var entry in entries.Values) Notify(entry.Snapshot);
    }
    private static void ValidateWorkspaceId(string workspaceId)
    { if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 256) throw new UiException("invalid_workspace", "A stable private workspace identity is required."); }
}
