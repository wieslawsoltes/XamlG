using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Styling;

/// <summary>Checks declared template parts in the template's own deferred namescope.</summary>
public sealed class AvaloniaTemplatePartsRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        if (!target.Type.HasMetadataName(AvaloniaStyleMetadata.ControlTemplate) ||
            !target.Annotations.TryGet(AvaloniaStyleAnnotations.TargetType, out var control)) return;
        var content = target.Assignments.OfType<BoundSetAssignment>().FirstOrDefault(assignment => assignment.Member.Name == "Content");
        var nameScope = (content?.Value as BoundDeferredExpression)?.NameScopeId;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var type = control; type != null; type = type.BaseType)
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass?.Name != "TemplatePartAttribute") continue;
                var name = Argument(attribute, "Name", 0) as string;
                if (string.IsNullOrEmpty(name) || !names.Add(name!)) continue;
                var expected = Argument(attribute, "Type", 1) as ITypeSymbol;
                var required = Argument(attribute, "IsRequired") is true;
                var actual = nameScope is { } scope ? context.FindName(scope, name!) : null;
                if (actual == null)
                {
                    context.Report(required ? "XG3113" : "XG3114",
                        (required ? "Required" : "Optional") + " template part '" + name + "' is missing from the ControlTemplate for '" + control.Name + "'.",
                        target.Syntax.NameSpan, required ? XamlSeverity.Error : XamlSeverity.Info);
                }
                else if (expected != null)
                {
                    var conversion = context.Types.Compilation.ClassifyConversion(actual, expected);
                    if (!conversion.IsImplicit || conversion.IsUserDefined || conversion.IsNumeric)
                        context.Report("XG3115", "Template part '" + name + "' must be assignable to '" + expected + "', but has type '" + actual + "'.",
                            context.FindNameSpan(nameScope!.Value, name!) ?? target.Syntax.NameSpan);
                }
            }
    }

    private static object? Argument(AttributeData attribute, string name, int position = -1)
    {
        foreach (var argument in attribute.NamedArguments)
            if (argument.Key == name) return argument.Value.Value;
        return position >= 0 && position < attribute.ConstructorArguments.Length ? attribute.ConstructorArguments[position].Value : null;
    }
}
