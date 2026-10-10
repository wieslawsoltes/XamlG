namespace XamlG.IntelligentUI;

/// <summary>Whole-surface budgets prevent repeated templates from multiplying per-control style limits.</summary>
public static class UiStyleTree
{
    public static void Validate(IEnumerable<UiElement> roots)
    {
        var brushes = new HashSet<string>(StringComparer.Ordinal); long brushText = 0;
        void BrushBudget(string name, System.Text.Json.JsonElement value)
        {
            if (!UiBrushValues.IsProperty(name) || value.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            var brush = UiBrushValues.Read(value)!; if (brush.Kind == "solid") return;
            var source = value.GetRawText(); if (!brushes.Add(source)) return;
            if (brushes.Count > 512 || (brushText += source.Length) > 1048576) throw new UiException("invalid_brush", "Whole-surface brush budget exceeded.");
        }
        var nodes = 0; var rules = 0; var setters = 0; long text = 0;
        foreach (var node in UiSessionStore.Flatten(roots))
        {
            if (++nodes > 4096 || node.Styles.IsDefault) throw new UiException("invalid_style", "Invalid styled tree.");
            foreach (var property in node.Properties) BrushBudget(property.Key, property.Value);
            rules += node.Styles.Length;
            if (rules > 512) throw new UiException("invalid_style", "A surface may contain at most 512 style rules.");
            foreach (var rule in node.Styles)
            {
                if (rule is null || rule.Properties is null) throw new UiException("invalid_style", "Invalid style rule.");
                setters += rule.Properties.Count; text += rule.Selector?.Length ?? 0;
                foreach (var pair in rule.Properties) { text += pair.Value.GetRawText().Length; BrushBudget(pair.Key, pair.Value); }
                if (setters > 4096 || text > 1048576) throw new UiException("invalid_style", "Whole-surface style budget exceeded.");
            }
        }
    }
}
