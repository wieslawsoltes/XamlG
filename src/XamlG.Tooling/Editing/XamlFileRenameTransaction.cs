using XamlG.Tooling.Refactoring;

namespace XamlG.Tooling.Editing;

/// <summary>Publishes source replacements and document identities as one optimistic history entry.
/// Hosts remain responsible for filesystem/project-file operations outside this in-memory transaction.</summary>
public static class XamlFileRenameTransaction
{
    public static XamlWorkspaceSnapshot ApplyFileRename(this XamlWorkspaceEditSession session, long expectedRevision,
        XamlFileRenamePlan plan, Action<XamlWorkspaceSnapshot>? validate = null)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var before = session.Current;
        if (before.Revision != expectedRevision) throw new InvalidOperationException("The file move belongs to a stale workspace revision.");
        var builder = before.Documents.ToBuilder();
        foreach (var original in plan.OriginalDocuments)
        {
            if (!builder.TryGetValue(original.Syntax.Path, out var text) || text != original.Syntax.Text)
                throw new InvalidOperationException("The resource graph changed after planning: " + original.Syntax.Path);
            builder.Remove(original.Syntax.Path);
        }
        var occupied = new HashSet<string>(builder.Keys.Select(Normalize), plan.PathComparer);
        foreach (var updated in plan.UpdatedDocuments)
        {
            if (!occupied.Add(Normalize(updated.Syntax.Path))) throw new InvalidOperationException("The destination is occupied by another workspace buffer: " + updated.Syntax.Path);
            builder.Add(updated.Syntax.Path, updated.Syntax.Text);
        }
        var candidate = new XamlWorkspaceSnapshot(checked(expectedRevision + 1), builder.ToImmutable());
        validate?.Invoke(candidate);
        // ReplaceAll performs budget checks and rechecks the revision under the publication gate.
        return session.ReplaceAll(expectedRevision, candidate.Documents, "Move XAML documents and update resource references");
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
}
