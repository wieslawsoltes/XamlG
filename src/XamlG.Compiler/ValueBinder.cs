using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Resolves conversions to typed operations. Only framework-independent primitive parsing runs at compile time.</summary>
public sealed class ValueBinder
{
    private readonly BindingContext _context;
    private readonly MarkupBinder _markup;
    public ValueBinder(BindingContext context) { _context = context; _markup = new(context); }
    public BoundExpression? BindText(string text, ITypeSymbol target, NamespaceScope scope, TextSpan span, ISymbol? member = null)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        if (text.StartsWith("{}", StringComparison.Ordinal)) text = text.Substring(2);
        else if (text.StartsWith("{", StringComparison.Ordinal))
        {
            var syntax = MarkupExtensionParser.Parse(text, span, _context.Diagnostics.Add);
            var value = syntax == null ? null : _markup.Bind(syntax, target, scope);
            return value == null ? null : Coerce(value, target, span);
        }
        var converted = TryText(text, target, scope, span, member);
        if (converted == null) _context.Report("XG1008", $"Cannot convert '{text}' to '{target.ToDisplayString()}'.", span);
        return converted;
    }
    public BoundExpression? TryText(string text, ITypeSymbol target, NamespaceScope scope, TextSpan span, ISymbol? member = null)
    {
        var propertyConverter = FindConverter(member);
        if (propertyConverter != null) return new BoundConverterExpression(text, propertyConverter, target, span);
        foreach (var rule in _context.Profile.TextConversionRules)
            if (rule.TryConvert(_context, text, target, scope, span, member, out var result)) return result;
        if (target is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        { var inner = TryText(text, nullable.TypeArguments[0], scope, span, member); return inner == null ? null : new BoundCastExpression(inner, target, span); }
        if (PrimitiveValueParser.TryParse(text, target.SpecialType, out var primitive))
            return new BoundConstantExpression(primitive, target.SpecialType == SpecialType.System_Object ? _context.Types.Special(SpecialType.System_String) : target, span);
        if (target.SpecialType is SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal) return null;
        if (target.TypeKind == TypeKind.Enum && target is INamedTypeSymbol enumeration)
        {
            var fields = ImmutableArray.CreateBuilder<IFieldSymbol>();
            foreach (var name in text.Split(','))
            {
                var field = enumeration.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (field == null)
                {
                    if (fields.Count == 0 && enumeration.EnumUnderlyingType != null && PrimitiveValueParser.TryParse(text, enumeration.EnumUnderlyingType.SpecialType, out var number))
                        return new BoundCastExpression(new BoundConstantExpression(number, enumeration.EnumUnderlyingType, span), target, span);
                    return null;
                }
                fields.Add(field);
            }
            return fields.Count == 0 ? null : new BoundEnumExpression(fields.ToImmutable(), target, span);
        }
        if (target.HasMetadataName(ClrNames.Type))
        {
            var referenced = ResolveTypeLiteral(text, scope, span, report: false);
            return referenced == null ? null : new BoundTypeExpression(referenced, target, span);
        }
        var converter = FindConverter(member) ?? FindConverter(target);
        if (converter != null) return new BoundConverterExpression(text, converter, target, span);
        var parse = target.Members(ClrNames.Parse).OfType<IMethodSymbol>()
            .Where(m => m.IsStatic && !m.IsGenericMethod && _context.Types.IsAccessible(m) && m.Parameters.Length is 1 or 2 &&
                m.Parameters[0].Type.SpecialType == SpecialType.System_String && (m.Parameters.Length == 1 || m.Parameters[1].Type.HasMetadataName(ClrNames.IFormatProvider) || m.Parameters[1].Type.HasMetadataName(ClrNames.CultureInfo)) &&
                _context.Types.Compilation.ClassifyCommonConversion(m.ReturnType, target).IsImplicit)
            .OrderByDescending(m => m.Parameters.Length).FirstOrDefault();
        if (parse != null) return new BoundParseExpression(text, parse, target, span);
        if (target.HasMetadataName(ClrNames.Uri) && target is INamedTypeSymbol uri)
        {
            var constructor = uri.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 2 && c.Parameters[0].Type.SpecialType == SpecialType.System_String && c.Parameters[1].Type.TypeKind == TypeKind.Enum);
            var relative = constructor?.Parameters[1].Type.GetMembers("RelativeOrAbsolute").OfType<IFieldSymbol>().FirstOrDefault();
            if (constructor != null && relative != null) return new BoundNewExpression(constructor, ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(text, _context.Types.Special(SpecialType.System_String), span), new BoundStaticExpression(relative, relative.Type, span)), span);
        }
        return null;
    }
    private INamedTypeSymbol? FindConverter(ISymbol? symbol)
    {
        if (symbol == null) return null;
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass == null || !_context.Types.Configuration.TypeConverterAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
            var value = attribute.ConstructorArguments.FirstOrDefault().Value;
            var type = value as INamedTypeSymbol ?? (value is string name ? _context.Types.Find(name.Split(',')[0].Trim()) : null);
            if (type == null || !_context.Types.IsAccessible(type) || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && _context.Types.IsAccessible(c))) continue;
            for (var current = type; current != null; current = current.BaseType) if (current.HasMetadataName(ClrNames.TypeConverter)) return type;
        }
        return null;
    }
    public ITypeSymbol? ResolveTypeLiteral(string name, NamespaceScope scope, TextSpan span, string? typeArguments = null, bool report = true)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = ResolveTypeLiteral(name.Substring(0, name.Length - 2), scope, span, typeArguments, report);
            return element == null ? null : _context.Types.Compilation.CreateArrayTypeSymbol(element);
        }
        return _context.ResolveType(name, scope, span, typeArguments, report);
    }
    public BoundExpression? BindNode(XamlSyntaxNode syntax, ITypeSymbol target, NamespaceScope scope, int nameScope = 0, bool normalizeText = true)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        if (syntax is XamlTextSyntax text) return BindText(normalizeText ? XmlWhitespace.Normalize(text.Value, scope.PreserveSpace) : text.Value, target, scope, syntax.Span);
        if (syntax is not XamlElementSyntax element) return null;
        foreach (var rule in _context.Profile.ObjectExpressionRules)
            if (rule.TryBind(_context, element, target, scope, nameScope, out var replacement))
                return replacement == null ? null : Coerce(replacement, target, element.Span);
        var nested = scope.Push(element); var name = nested.Expand(element.Name);
        if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
        {
            if (name.LocalName == "Null") return Coerce(new BoundConstantExpression(null, null, syntax.Span), target, syntax.Span);
            if (name.LocalName == "Array")
            {
                var typeText = element.Attributes.FirstOrDefault(a => a.Name == "Type")?.Value;
                ITypeSymbol? itemType = null;
                if (typeText != null)
                {
                    var typeValue = BindText(typeText, _context.Types.Find(ClrNames.Type)!, nested, element.NameSpan);
                    itemType = (typeValue as BoundTypeExpression)?.ReferencedType;
                }
                itemType ??= (target as IArrayTypeSymbol)?.ElementType;
                if (itemType == null) { _context.Report("XG1021", "x:Array requires an element Type.", syntax.Span); return null; }
                var values = ImmutableArray.CreateBuilder<BoundExpression>();
                foreach (var child in element.Children.Where(c => c is XamlElementSyntax || c is XamlTextSyntax t && !string.IsNullOrWhiteSpace(t.Value)))
                { var value = BindNode(child, itemType, nested, nameScope); if (value != null) values.Add(value); }
                return Coerce(new BoundArrayExpression(values.ToImmutable(), _context.Types.Compilation.CreateArrayTypeSymbol(itemType), syntax.Span), target, syntax.Span);
            }
            if (name.LocalName is "Type" or "Static" or "Reference" or "True" or "False")
            {
                var args = element.Attributes.Where(a => !a.IsNamespace).Select(a => new MarkupArgumentSyntax(a.Name, a.Value, a.Span)).ToImmutableArray();
                var value = _markup.Bind(new(element.Name, args, element.Span), target, nested); return value == null ? null : Coerce(value, target, syntax.Span);
            }
        }
        var type = _context.ResolveType(element.Name, nested, element.NameSpan, nested.Directive(element, "TypeArguments")?.Value);
        if (type == null) return null;
        if (!element.Children.OfType<XamlElementSyntax>().Any())
        {
            var content = string.Concat(element.Children.OfType<XamlTextSyntax>().Select(t => t.Value));
            if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum || type.IsValueType || type.HasMetadataName(ClrNames.Uri))
            {
                var value = TryText(type.SpecialType == SpecialType.System_String ? content : XmlWhitespace.Normalize(content, nested.PreserveSpace), type, nested, element.Span);
                if (value != null) return Coerce(value, target, syntax.Span);
            }
        }
        var bound = _context.Objects.Bind(element, scope, type, false, nameScope);
        if (bound == null) return null;
        var provided = _markup.Provide(bound, syntax.Span);
        return Coerce(provided ?? new BoundObjectExpression(bound), target, syntax.Span);
    }
    public ITypeSymbol? PeekNodeType(XamlSyntaxNode syntax, NamespaceScope scope)
    {
        if (syntax is XamlTextSyntax) return _context.Types.Special(SpecialType.System_String);
        if (syntax is not XamlElementSyntax element) return null;
        var nested = scope.Push(element); var name = nested.Expand(element.Name);
        if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName == "Null") return null;
        return _context.ResolveType(element.Name, nested, element.NameSpan, nested.Directive(element, "TypeArguments")?.Value, report: false);
    }
    public ITypeSymbol? PeekValueType(XamlSyntaxNode syntax, NamespaceScope scope)
    {
        var type = PeekNodeType(syntax, scope);
        return type == null ? null : _context.Types.MarkupExtensionMethod(type)?.ReturnType ?? type;
    }
    public bool TryGetStringLiteral(XamlSyntaxNode syntax, NamespaceScope scope, out string value)
    {
        if (syntax is XamlTextSyntax text) { value = text.Value; return true; }
        if (syntax is XamlElementSyntax element && PeekNodeType(element, scope)?.SpecialType == SpecialType.System_String)
        { value = string.Concat(element.Children.OfType<XamlTextSyntax>().Select(t => t.Value)); return true; }
        value = string.Empty; return false;
    }
    public BoundExpression? Coerce(BoundExpression value, ITypeSymbol target, TextSpan span)
    {
        if (value.Type == null)
        {
            if (target.AcceptsNull()) return new BoundConstantExpression(null, target, span);
            _context.Report("XG1022", $"Null cannot be assigned to '{target}'.", span); return null;
        }
        var conversion = _context.Types.Compilation.ClassifyConversion(value.Type, target);
        if (conversion.IsImplicit && (!conversion.IsNumeric || _context.Types.Configuration.AllowImplicitNumericConversions)) return value;
        if (conversion.Exists && value.Type.SpecialType == SpecialType.System_Object) return new BoundCastExpression(value, target, span);
        if (value is BoundConstantExpression { Value: string text } && TryText(text, target, NamespaceScope.Empty, span) is { } converted) return converted;
        _context.Report("XG1023", $"Value of type '{value.Type}' cannot be assigned to '{target}'.", span); return null;
    }
}
