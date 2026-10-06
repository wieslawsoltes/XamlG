using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler.Options;

/// <summary>Symbol-only lowering for metadata-declared option extensions. The receiver
/// and predicates execute at runtime, and only the selected value is evaluated.</summary>
public sealed class XamlOptionMarkupBinder(XamlOptionMarkupConfiguration configuration)
{
    public bool TryBind(BindingContext context, XamlElementSyntax syntax, INamedTypeSymbol type,
        NamespaceScope scope, int nameScope, ITypeSymbol targetType, ImmutableArray<XamlSyntaxNode> positional,
        out BoundExpression? expression)
    {
        expression = null;
        var predicates = type.Members(configuration.PredicateMethod).OfType<IMethodSymbol>()
            .Where(m => !m.IsGenericMethod && m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                m.Parameters.Length is 1 or 2 && (m.Parameters.Length == 1 ||
                m.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider)) && context.Types.IsAccessible(m)).ToArray();
        if (predicates.Length == 0) return false;
        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                if (!properties.ContainsKey(property.Name)) properties.Add(property.Name, property);
        if (!properties.Values.Any(p => Attribute(p, configuration.OptionAttribute) != null ||
                                      Attribute(p, configuration.DefaultAttribute) != null)) return false;

        var resultType = type.TypeArguments.FirstOrDefault() ?? targetType;
        var branches = ImmutableArray.CreateBuilder<BoundChoiceBranch>();
        BoundExpression? fallback = null;
        var hasFallback = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var remainingAttributes = ImmutableArray.CreateBuilder<XamlAttributeSyntax>();
        var remainingChildren = ImmutableArray.CreateBuilder<XamlSyntaxNode>();
        void Error(string message, TextSpan span) => context.Report("XG1040", message, span);
        BoundExpression? Value(ImmutableArray<XamlSyntaxNode> nodes, NamespaceScope valueScope, TextSpan span)
        {
            var content = nodes.Where(n => n is XamlElementSyntax || n is XamlTextSyntax).ToArray();
            if (content.Any(n => n is XamlElementSyntax))
                content = content.Where(n => n is not XamlTextSyntax t || !string.IsNullOrWhiteSpace(t.Value)).ToArray();
            else if (content.Length > 1)
                content = new XamlSyntaxNode[] { new XamlTextSyntax(string.Concat(content.Cast<XamlTextSyntax>().Select(t => t.Value)), false, span) };
            if (content.Length != 1) { Error("An option requires exactly one value.", span); return null; }
            return context.Values.BindNode(content[0], resultType, valueScope, nameScope);
        }
        bool Add(string name, ImmutableArray<XamlSyntaxNode> nodes, NamespaceScope valueScope, TextSpan span)
        {
            if (!properties.TryGetValue(name, out var property)) return false;
            var option = Attribute(property, configuration.OptionAttribute);
            var isDefault = Attribute(property, configuration.DefaultAttribute) != null;
            if (option == null && !isDefault) return false;
            if (!seen.Add(name)) { Error("The option is assigned more than once: " + name, span); return true; }
            var value = Value(nodes, valueScope, span);
            if (value == null) return true;
            if (isDefault) { fallback = value; hasFallback = true; return true; }
            if (option!.ConstructorArguments.Length != 1 || option.ConstructorArguments[0].IsNull)
            { Error("Option metadata requires one non-null value.", span); return true; }
            var optionValue = option.ConstructorArguments[0];
            var candidates = new List<(IMethodSymbol Method, BoundExpression Value, int Rank)>();
            foreach (var predicate in predicates)
            {
                var parameter = predicate.Parameters[predicate.Parameters.Length - 1].Type;
                var converted = Constant(context, optionValue, parameter, valueScope, span);
                if (converted != null) candidates.Add((predicate, converted,
                    SymbolEqualityComparer.Default.Equals(optionValue.Type, parameter) ? 0 : 1));
            }
            var ranked = candidates.OrderBy(c => c.Rank).ToArray();
            if (ranked.Length == 0 || ranked.Length > 1 && ranked[0].Rank == ranked[1].Rank)
            { Error("The option predicate is missing or ambiguous for '" + name + "'.", span); return true; }
            branches.Add(new(ranked[0].Method, ranked[0].Value, value));
            return true;
        }
        if (!positional.IsDefaultOrEmpty)
        {
            var defaults = properties.Values.Where(p => Attribute(p, configuration.DefaultAttribute) != null).ToArray();
            if (positional.Length != 1 || defaults.Length != 1)
                Error("Options extensions accept one positional default value.", syntax.Span);
            else Add(defaults[0].Name, positional, scope, syntax.Span);
        }
        foreach (var attribute in syntax.Attributes)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (attribute.IsNamespace || !Add(attribute.Name,
                ImmutableArray.Create<XamlSyntaxNode>(new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan)), scope, attribute.ValueSpan))
                remainingAttributes.Add(attribute);
        }
        foreach (var child in syntax.Children)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (child is not XamlElementSyntax element)
            {
                if (child is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value))
                    Error("An options extension requires named option entries.", child.Span);
                continue;
            }
            var nested = scope.Push(element);
            var dot = element.LocalName.LastIndexOf('.');
            if (dot >= 0)
            {
                if (!Add(element.LocalName.Substring(dot + 1), element.Children, nested, element.Span))
                    remainingChildren.Add(element);
                continue;
            }
            var entryType = context.Values.PeekNodeType(element, scope) as INamedTypeSymbol;
            if (entryType == null || !configuration.EntryTypes.Any(entryType.OriginalDefinition.HasMetadataName))
            { Error("An options extension contains an unsupported entry.", element.NameSpan); continue; }
            var options = element.Attributes.FirstOrDefault(a => a.Name == configuration.OptionsProperty)?.Value;
            var contentAttribute = element.Attributes.FirstOrDefault(a => a.Name == configuration.ContentProperty);
            var contentProperty = element.Children.OfType<XamlElementSyntax>().FirstOrDefault(e =>
                e.LocalName.EndsWith("." + configuration.ContentProperty, StringComparison.Ordinal));
            var content = contentAttribute != null ? ImmutableArray.Create<XamlSyntaxNode>(
                new XamlTextSyntax(contentAttribute.Value, false, contentAttribute.ValueSpan)) : contentProperty?.Children ?? element.Children;
            var labels = options?.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (labels.Length == 0) { Error("An option entry requires at least one option name.", element.Span); continue; }
            foreach (var label in labels)
                if (!Add(label, content, contentProperty == null ? nested : nested.Push(contentProperty), element.Span))
                    Error("Unknown option name: " + label, element.Span);
        }
        if (!hasFallback && branches.Count == 0) Error("An options extension requires at least one option.", syntax.Span);
        var receiverSyntax = syntax with { Attributes = remainingAttributes.ToImmutable(), Children = remainingChildren.ToImmutable() };
        var receiver = context.Objects.Bind(receiverSyntax, scope, type, false, nameScope);
        if (receiver != null) expression = new BoundChoiceExpression(receiver, branches.ToImmutable(), fallback, resultType, syntax.Span);
        return true;
    }

    private static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.HasMetadataName(name) == true);

    private static BoundExpression? Constant(BindingContext context, TypedConstant value, ITypeSymbol target,
        NamespaceScope scope, TextSpan span)
    {
        BoundExpression? expression = value.Kind == TypedConstantKind.Type && value.Value is ITypeSymbol type
            ? new BoundTypeExpression(type, context.Types.Find(ClrNames.Type)!, span)
            : value.Type == null ? null : value.Type.TypeKind == TypeKind.Enum && value.Type is INamedTypeSymbol enumeration
                ? new BoundCastExpression(new BoundConstantExpression(value.Value, enumeration.EnumUnderlyingType, span), value.Type, span)
                : new BoundConstantExpression(value.Value, value.Type, span);
        if (expression?.Type != null && context.Types.Compilation.ClassifyCommonConversion(expression.Type, target).IsImplicit)
            return expression;
        return context.Values.TryText(Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty, target, scope, span);
    }
}
