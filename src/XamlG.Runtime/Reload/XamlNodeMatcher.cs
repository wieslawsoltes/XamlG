namespace XamlG.Runtime.Reload;

/// <summary>Matches explicit identities first, then unique unchanged declarations. Positional anonymous IDs are never trusted across revisions.</summary>
public static class XamlNodeMatcher
{
    public static IReadOnlyList<XamlStateTransferContext> Match(XamlRuntimeSession previous, XamlRuntimeSession candidate,
        object previousRoot, object candidateRoot)
    {
        var oldNodes = previous.Nodes.Where(n => n.Source != null).ToArray();
        var newNodes = candidate.Nodes.Where(n => n.Source != null).ToArray();
        var result = new List<XamlStateTransferContext>();
        var matched = new HashSet<object>(XamlObjectIdentityComparer.Instance);
        var oldIdentities = Unique(oldNodes.Where(n => n.Source!.Identity != null), n => n.Source!.Path + "\0" + n.Source.Identity);
        var newIdentities = Unique(newNodes.Where(n => n.Source!.Identity != null), n => n.Source!.Path + "\0" + n.Source.Identity);
        var oldFingerprints = Unique(oldNodes.Where(n => n.Source!.Identity == null), n => n.Source!.Path + "\0" + n.Source.Fingerprint);
        var newFingerprints = Unique(newNodes.Where(n => n.Source!.Identity == null), n => n.Source!.Path + "\0" + n.Source.Fingerprint);
        foreach (var next in newNodes)
        {
            XamlRuntimeNode? old = null;
            if (ReferenceEquals(next.Instance, candidateRoot))
                old = oldNodes.FirstOrDefault(n => ReferenceEquals(n.Instance, previousRoot));
            else if (next.Source!.Identity is { } identity)
            {
                var key = next.Source.Path + "\0" + identity;
                if (newIdentities.ContainsKey(key)) oldIdentities.TryGetValue(key, out old);
            }
            else
            {
                var key = next.Source!.Path + "\0" + next.Source.Fingerprint;
                if (newFingerprints.ContainsKey(key)) oldFingerprints.TryGetValue(key, out old);
            }
            if (old != null && old.Instance.GetType().FullName == next.Instance.GetType().FullName && matched.Add(old.Instance))
                result.Add(new(old, next));
        }
        return result;
    }
    private static Dictionary<string, XamlRuntimeNode> Unique(IEnumerable<XamlRuntimeNode> nodes, Func<XamlRuntimeNode, string> key) =>
        nodes.GroupBy(key, StringComparer.Ordinal).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
}
