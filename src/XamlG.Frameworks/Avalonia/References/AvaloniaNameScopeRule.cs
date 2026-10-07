using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;

namespace XamlG.Frameworks.Avalonia.References;

/// <summary>Registers literal INamed.Name assignments as well as x:Name.
/// Registration is emitted into both the compiler and framework namescopes, including
/// the independent namescope belonging to each deferred template instance.</summary>
public sealed class AvaloniaNameScopeRule : IXamlObjectBindingRule
{
    public void Initialize(BindingContext context, ObjectBindingBuilder target)
    {
        if (AvaloniaLiteralName.Read(context, target) is not { } name) return;
        if (!SyntaxFacts.IsValidIdentifier(name.Value))
        {
            context.Report("XG1012", $"Invalid XAML name '{name.Value}'.", name.Span);
            return;
        }
        if (target.Name != null)
        {
            if (!string.Equals(target.Name, name.Value, StringComparison.Ordinal))
                context.Report("XG1012", "Name and x:Name must identify the same object.", name.Span);
            return;
        }
        target.Name = name.Value;
        context.RegisterName(target.NameScopeId, name.Value, target.Type, name.Span);
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target) { }
}
