using System.Text.Json;

namespace XamlG.IntelligentUI;

/// <summary>Non-executing normalization for explicitly reviewed C# declarations.
/// It validates data and effect boundaries without parsing, compiling or evaluating expressions.</summary>
public static class UiCSharpDeclaration
{
    public static UiPublish Normalize(UiPublish request, UiLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        limits ??= new(); limits.Validate();
        UiJson.Identifier(request.Id, "Surface ID");
        if (request.Xaml == null || request.Xaml.Length > limits.SourceCharacters || !request.IsFinal ||
            request.FallbackMarkdown?.Length > limits.TextCharacters)
            throw new UiException("invalid_proposal", "Supply a complete bounded declaration.");
        var state = UiJson.Object(request.InitialState, limits.DataBytes, "State");
        var data = UiJson.Object(request.Data, limits.DataBytes, "Data");
        var sourceActions = request.Actions ?? [];
        if (sourceActions.Length > limits.Actions) throw new UiException("action_limit", "Too many local state actions.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var actions = new UiAction[sourceActions.Length];
        for (var index = 0; index < sourceActions.Length; index++)
        {
            var action = sourceActions[index];
            if (action == null || action.Kind != "state" || action.Text != null || action.Tool != null ||
                action.Arguments is not { ValueKind: JsonValueKind.Object } arguments)
                throw new UiException("invalid_proposal", "Full-C# declarations allow only bounded local state actions, never external effects.");
            UiJson.Identifier(action.Id, "Action ID");
            if (!ids.Add(action.Id)) throw new UiException("invalid_proposal", "Duplicate local action ID.");
            arguments = UiJson.Object(arguments, limits.DataBytes, "Local state action");
            var fields = arguments.EnumerateObject().ToArray();
            if (fields.Length == 0 || fields.Length > limits.StateKeys)
                throw new UiException("invalid_proposal", "Local actions need a nonempty bounded state patch.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                UiJson.Identifier(field.Name, "State key");
                if (!keys.Add(field.Name) || !state.TryGetProperty(field.Name, out _))
                    throw new UiException("invalid_proposal", "Action targets must be unique declared state keys.");
                CheckText(field.Value, limits.TextCharacters);
            }
            actions[index] = action with { Arguments = arguments.Clone() };
        }
        UiDataStore.ValidateJson(JsonSerializer.SerializeToElement(actions), limits.DataBytes);
        return request with { ExpectedRevision = 0, Sequence = 1, InitialState = state, Data = data, Actions = actions };
    }
    private static void CheckText(JsonElement value, int limit)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > limit)
            throw new UiException("text_limit", "Local action text exceeds the expression review limit.");
        if (value.ValueKind == JsonValueKind.Object) foreach (var field in value.EnumerateObject()) CheckText(field.Value, limit);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckText(item, limit);
    }
}
