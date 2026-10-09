namespace XamlG.IntelligentUI;

/// <summary>Non-executing action classification shared by native UI hosts. Classification
/// never grants authority: execute through the owner-scoped store or authorized MCP tool.</summary>
public static class UiActionRouting
{
    public static bool IsStateAction(UiSnapshot snapshot, UiActionCall call)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(call);
        if (snapshot.Id != call.Id || snapshot.Revision != call.ExpectedRevision || snapshot.StateRevision != call.ExpectedStateRevision)
            throw new UiException("revision_conflict", "The UI changed before its action was routed.");
        var node = UiSessionStore.Flatten(snapshot.Roots).SingleOrDefault(node => node.Key == call.NodeKey);
        if (node?.ActionId == null) throw new UiException("invalid_action", "The node does not declare an action.");
        var action = snapshot.Actions.SingleOrDefault(action => action.Id == node.ActionId)
            ?? throw new UiException("invalid_action", "The node's action is not declared.");
        return action.Kind == "state";
    }
}
