using Microsoft.CodeAnalysis;
using System.Threading;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia;

/// <summary>Separates build-task document directives from directives discarded by runtime compilation.</summary>
public sealed class AvaloniaDirectivePolicy : IXamlDirectivePolicy, IXamlBindingRule
{
    public bool ShouldCompile(XamlSyntaxTree syntax, XamlCompilerOptions options) =>
        !options.IsPrecompilation || Precompile(syntax)?.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) != true;

    public IEnumerable<XamlDiagnostic> GetSkippedDiagnostics(XamlSyntaxTree syntax, CancellationToken cancellationToken) =>
        AvaloniaDirectiveSyntax.ValidateSkipped(syntax, cancellationToken);

    public string BindClassModifier(BindingContext context, XamlAttributeSyntax? directive)
    {
        var actual = context.RootClass?.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        if (!context.Options.IsPrecompilation) return context.RootClass == null ? "public" : actual;
        if (Precompile(context.Syntax) is { } precompile && !precompile.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            context.Report("XG1018", "x:Precompile must be true or false.", precompile.ValueSpan);
        if (directive == null) return context.RootClass == null ? "public" : actual;
        var modifier = directive.Value.Trim().ToLowerInvariant() switch
        {
            "public" => "public",
            "internal" or "notpublic" => "internal",
            _ => null
        };
        if (modifier == null)
            context.Report("XG1018", "x:ClassModifier must be Public, NotPublic or internal.", directive.ValueSpan);
        else if (context.RootClass != null && modifier != actual)
            context.Report("XG1018", "x:ClassModifier does not match the x:Class type's accessibility.", directive.ValueSpan);
        return modifier ?? actual;
    }

    public string BindFieldModifier(BindingContext context, XamlAttributeSyntax? directive) => directive?.Value.ToLowerInvariant() switch
    {
        "public" => "public",
        "private" => "private",
        "protected" => "protected",
        "internal" or "notpublic" => "internal",
        _ => "internal"
    };

    public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        if (scope.Expand(attribute.Name, true) is not { Namespace: XamlNames.Language2006, LocalName: "Precompile" or "Class" or "ClassModifier" or "FieldModifier" }) return false;
        AvaloniaDirectiveSyntax.ValidateMarkup(attribute.Value, attribute.ValueSpan, scope, context.Diagnostics.Add, context.Cancellation);
        return true;
    }

    private static XamlAttributeSyntax? Precompile(XamlSyntaxTree syntax)
    {
        if (syntax.Root is not { } root) return null;
        var scope = NamespaceScope.Empty.Push(root);
        return root.Attributes.FirstOrDefault(attribute => scope.Expand(attribute.Name, true) is
            { Namespace: XamlNames.Language2006, LocalName: "Precompile" });
    }
}
