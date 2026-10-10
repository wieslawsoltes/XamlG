using System.Collections.Immutable;
using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed partial class UiSessionStore
{
    private ImmutableArray<UiAction> ValidateActions(UiAction[] actions)
    {
        if (actions.Length > Compiler.Limits.Actions) throw new UiException("action_limit", "Too many actions.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var result = ImmutableArray.CreateBuilder<UiAction>();
        foreach (var action in actions)
        {
            if (action == null) throw new UiException("invalid_action", "Null action.");
            UiJson.Identifier(action.Id, "Action ID");
            if (!ids.Add(action.Id) || action.Kind is not ("message" or "copy" or "openUrl" or "tool" or "state"))
                throw new UiException("invalid_action", "Invalid or duplicate action.");
            if (action.Kind == "tool")
            {
                if (string.IsNullOrEmpty(action.Tool) || action.Tool.Length > 64 || !action.Tool.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    throw new UiException("invalid_action", "Tool name is invalid.");
                if (action.Text != null) throw new UiException("invalid_action", "Tool actions do not accept Text.");
            }
            else if (action.Kind == "state")
            {
                if (action.Text != null || action.Tool != null || action.Arguments is not { ValueKind: JsonValueKind.Object })
                    throw new UiException("invalid_action", "State actions require only an Arguments object of state replacements.");
            }
            else if (action.Text == null || action.Tool != null || action.Arguments != null)
                throw new UiException("invalid_action", "This action requires only Text.");
            if (action.Text != null) ValidateString(action.Text);
            JsonElement? arguments = action.Arguments is { } args ? UiJson.Object(args, Compiler.Limits.DataBytes, "Action arguments") : null;
            if (arguments is { } argumentObject)
            {
                ValidateArgumentExpressions(argumentObject);
                if (action.Kind == "state")
                {
                    var fields = argumentObject.EnumerateObject().ToArray();
                    if (fields.Length == 0 || fields.Length > Compiler.Limits.StateKeys)
                        throw new UiException("invalid_action", "State actions require a nonempty bounded patch.");
                    foreach (var field in fields) UiJson.Identifier(field.Name, "State key");
                }
            }
            result.Add(action with { Arguments = arguments });
        }
        return result.ToImmutable();
    }
    private void ValidateArgumentExpressions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) ValidateString(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Object) foreach (var field in value.EnumerateObject()) ValidateArgumentExpressions(field.Value);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var field in value.EnumerateArray()) ValidateArgumentExpressions(field);
    }
    private void ValidateString(string value)
    {
        if (value.Length > Compiler.Limits.TextCharacters) throw new UiException("text_limit", "Action text exceeds its limit.");
        if (value.StartsWith("{ui:Expr ", StringComparison.Ordinal)) ParseExpression(value);
    }
    private IUiExpression ParseExpression(string value)
    {
        if (!value.EndsWith('}')) throw new UiException("invalid_expression", "Unterminated action expression.");
        return Compiler.CompileExpression(value[9..^1]);
    }
}
