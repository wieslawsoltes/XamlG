namespace XamlG.IntelligentUI;

internal sealed partial class UiCompositeLowerer
{
    private UiElement Form(UiElement node)
    {
        if (_form != null) throw new UiException("invalid_form", "Forms cannot be nested.");
        _form = FormValidation(node);
        try
        {
            var children = new List<UiElement>();
            if (Text(node, "Title") is { Length: > 0 } title) children.Add(Label(Key(node, "title"), title, 20, true));
            if (Text(node, "Description") is { Length: > 0 } description) children.Add(Label(Key(node, "description"), description));
            children.AddRange(node.Children.Select(LowerNode));
            return Stack(node.Key, children, Number(node, "Gap", 3) * 4, Outer(node));
        }
        finally { _form = null; }
    }

    private UiElement Field(UiElement node)
    {
        // Validate the declaration even when presentation is hidden or disabled.
        var validation = ValidateField(node);
        var input = LowerNode(node.Children[0]);
        var error = _form != null ? _form.Errors.GetValueOrDefault(node.Key)
            : Active(node) && Active(node.Children[0]) ? validation : null;
        var showErrors = Flag(node, "ShowErrors", true) && (_form?.ShowErrors ?? true);
        var label = Text(node, "Label", input.StateKey ?? "Value");
        var required = Flag(node, "IsRequired", false);
        var help = Text(node, "HelpText");
        var properties = input.Properties;
        if (!properties.ContainsKey("AutomationProperties.Name") && label.Length > 0)
            properties = properties.SetItem("AutomationProperties.Name", J(label + (required ? ", required" : "")));
        var hint = help;
        if (showErrors && error != null) hint = hint.Length == 0 ? error.Message : hint + "\n" + error.Message;
        if (!properties.ContainsKey("ToolTip.Tip") && hint.Length > 0) properties = properties.SetItem("ToolTip.Tip", J(hint));
        input = input with { Properties = properties };
        var children = new List<UiElement>();
        if (label.Length > 0) children.Add(Label(Key(node, "label"), label + (required ? " *" : ""), 14, true));
        children.Add(input);
        if (help.Length > 0) children.Add(Label(Key(node, "help"), help, 12));
        if (showErrors && error != null) children.Add(ErrorLabel(Key(node, "error"), error.Message));
        return Stack(node.Key, children, 4, Outer(node));
    }

    private UiElement SubmitButton(UiElement node)
    {
        if (_form == null) throw new UiException("invalid_form", "SubmitButton must be inside a Form.");
        var properties = Outer(node).SetItem("IsEnabled", J(_form.IsValid && Flag(node, "IsEnabled", true)));
        if (node.Properties.TryGetValue("Content", out var content)) properties = properties.SetItem("Content", content);
        return Make(node.Key, "Button", properties, node.Children.Select(LowerNode), actionId: node.ActionId);
    }

    private UiElement ValidationSummary(UiElement node)
    {
        if (_form == null) throw new UiException("invalid_form", "ValidationSummary must be inside a Form.");
        var children = new List<UiElement>();
        var visible = !_form.IsValid && _form.ShowErrors && Flag(node, "IsVisible", true);
        if (visible)
        {
            children.Add(Label(Key(node, "title"), Text(node, "Title", "Please correct the following:"), 14, true));
            if (_form.Error != null) children.Add(ErrorLabel(Key(node, "form-error"), _form.Error));
            var index = 0;
            foreach (var error in _form.Errors.Values)
                children.Add(ErrorLabel(Key(node, "field-" + index++), error.Label + ": " + error.Message));
        }
        return Stack(node.Key, children, 4, Outer(node).SetItem("IsVisible", J(visible)));
    }

    private UiElement ErrorLabel(string key, string message)
    {
        var label = Label(key, message, 12);
        return label with { Properties = label.Properties.SetItem("Foreground", J("Red")) };
    }
}
