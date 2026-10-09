using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    /// <summary>Resolve a declared action against the current owned tree and its compiler-captured item.</summary>
    public UiActionIntent PrepareAction(UiActionCall request, string principal)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var snapshot = Get(request.Id, principal).Snapshot;
            Revisions(snapshot, request.ExpectedRevision, request.ExpectedStateRevision);
            var node = Flatten(snapshot.Roots).SingleOrDefault(n => n.Key == request.NodeKey);
            if (node?.ActionId == null || Disabled(node, snapshot.Roots))
                throw new UiException("invalid_action", "No enabled action is exposed by this node.");
            var action = snapshot.Actions.Single(a => a.Id == node.ActionId);
            var text = action.Text == null ? null : ResolveScopedText(action.Text, snapshot, node.ActionItem);
            var arguments = action.Arguments is { } args ? ResolveScopedArguments(args, snapshot, node.ActionItem) : (JsonElement?)null;
            if (action.Kind == "openUrl" && (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)))
                throw new UiException("invalid_url", "Only absolute HTTP(S) URLs without credentials are allowed.");
            return new(snapshot.Id, snapshot.Revision, action.Id, action.Kind, text, action.Tool, arguments);
        }
    }

    private string ResolveScopedText(string value, UiSnapshot snapshot, JsonElement? actionItem)
    {
        var text = value.StartsWith("{ui:Expr ", StringComparison.Ordinal)
            ? UiJson.Text(UiJson.Value(ParseExpression(value).Evaluate(snapshot.State, snapshot.Data, actionItem))) : value;
        if (text.Length > Compiler.Limits.TextCharacters)
            throw new UiException("text_limit", "Resolved action text exceeds its limit.");
        return text;
    }

    private JsonElement ResolveScopedArguments(JsonElement value, UiSnapshot snapshot, JsonElement? actionItem)
    {
        object? Visit(JsonElement current) => current.ValueKind switch
        {
            JsonValueKind.String when current.GetString()!.StartsWith("{ui:Expr ", StringComparison.Ordinal)
                => ParseExpression(current.GetString()!).Evaluate(snapshot.State, snapshot.Data, actionItem),
            JsonValueKind.Object => current.EnumerateObject().ToDictionary(p => p.Name, p => Visit(p.Value), StringComparer.Ordinal),
            JsonValueKind.Array => current.EnumerateArray().Select(Visit).ToArray(),
            _ => current
        };
        return UiJson.Object(JsonSerializer.SerializeToElement(Visit(value)), Compiler.Limits.DataBytes, "Resolved action arguments");
    }
}
