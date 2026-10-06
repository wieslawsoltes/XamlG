using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Registers the literal StyledElement.Name spelling as well as x:Name.
/// Registration is emitted into both the compiler and framework namescopes, including
/// the independent namescope belonging to each deferred template instance.</summary>
public sealed class AvaloniaNameScopeRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        var styled = context.Types.Find(AvaloniaStyleMetadata.StyledElement);
        if (styled == null || !context.Types.Compilation.ClassifyCommonConversion(target.Type, styled).IsImplicit) return;
        var attribute = target.Syntax.Attributes.FirstOrDefault(a => a.Name == "Name");
        if (attribute == null) return;
        if (!SyntaxFacts.IsValidIdentifier(attribute.Value))
        {
            context.Report("XG1012", $"Invalid XAML name '{attribute.Value}'.", attribute.ValueSpan);
            return;
        }
        if (target.Name != null)
        {
            if (!string.Equals(target.Name, attribute.Value, StringComparison.Ordinal))
                context.Report("XG1012", "Name and x:Name must identify the same object.", attribute.ValueSpan);
            return;
        }
        target.Name = attribute.Value;
        context.RegisterName(target.NameScopeId, attribute.Value, target.Type, attribute.ValueSpan);
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target) { }
}
