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
            return value == null ? null : Coerce(value, target, span, scope, member);
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
        if (PrimitiveValueParser.IsScalar(target)) return null;
        if (target.TypeKind == TypeKind.Enum && target is INamedTypeSymbol enumeration && TryEnum(text, enumeration, span) is { } enumValue)
            return enumValue;
        if (target.HasMetadataName(ClrNames.Type))
        {
            var referenced = ResolveTypeLiteral(text, scope, span, report: false);
            return referenced == null ? null : new BoundTypeExpression(referenced, target, span);
        }
        if (target is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
        {
            var method = RootMethodBinder.Resolve(_context, text, delegateType);
            if (method != null)
            {
                _context.Symbols.Add(new(span, method, "method"));
                return new BoundMethodGroupExpression(method, null, delegateType, span);
            }
        }
        var parse = FindParse(target);
        if (parse != null) return new BoundParseExpression(text, parse, target, span);
        var converter = FindConverter(target);
        if (converter != null) return new BoundConverterExpression(text, converter, target, span);
        if (target.HasMetadataName(ClrNames.Uri) && target is INamedTypeSymbol uri)
        {
            var constructor = uri.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 2 && c.Parameters[0].Type.SpecialType == SpecialType.System_String && c.Parameters[1].Type.TypeKind == TypeKind.Enum);
            var relative = constructor?.Parameters[1].Type.GetMembers("RelativeOrAbsolute").OfType<IFieldSymbol>().FirstOrDefault();
            if (constructor != null && relative != null) return new BoundNewExpression(constructor, ImmutableArray.Create<BoundExpression>(new BoundConstantExpression(text, _context.Types.Special(SpecialType.System_String), span), new BoundStaticExpression(relative, relative.Type, span)), span);
        }
        return null;
    }
    public BoundExpression? TryConvert(BoundExpression value, ITypeSymbol target, NamespaceScope scope, TextSpan span, ISymbol? member = null, bool allowTextConversion = true)
    {
        var propertyConverter = FindConverter(member);
        if (allowTextConversion && value is BoundConstantExpression { Value: string text })
        {
            if (propertyConverter == null && _context.Types.Compilation.ClassifyCommonConversion(value.Type!, target).IsImplicit) return null;
            return TryText(text, target, scope, span, member);
        }
        if (propertyConverter != null) return new BoundValueConverterExpression(value, propertyConverter, target, span);
        if (value.Type?.SpecialType != SpecialType.System_String) return null;
        if (target is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = TryConvert(value, nullable.TypeArguments[0], scope, span, member, allowTextConversion);
            return inner == null ? null : new BoundCastExpression(inner, target, span);
        }
        var parse = FindParse(target);
        if (parse != null)
        {
            var arguments = ImmutableArray.Create(value);
            if (parse.Parameters.Length == 2)
            {
                var culture = _context.Types.Find(ClrNames.CultureInfo)!.GetMembers("InvariantCulture").OfType<IPropertySymbol>().Single();
                arguments = arguments.Add(new BoundStaticExpression(culture, culture.Type, span));
            }
            return new BoundCallExpression(parse, null, arguments, span);
        }
        var converter = FindConverter(target);
        return converter == null ? null : new BoundValueConverterExpression(value, converter, target, span);
    }
    public bool CanConvertValueType(ITypeSymbol source, ITypeSymbol target, ISymbol? member = null)
    {
        if (FindConverter(member) != null) return true;
        if (source.SpecialType != SpecialType.System_String) return false;
        if (target is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            target = nullable.TypeArguments[0];
        return FindParse(target) != null || FindConverter(target) != null;
    }
    private IMethodSymbol? FindParse(ITypeSymbol target) => target.Members(ClrNames.Parse).OfType<IMethodSymbol>()
            .Where(m => m.IsStatic && !m.IsGenericMethod && _context.Types.IsAccessible(m) && m.Parameters.Length is 1 or 2 &&
                m.Parameters[0].Type.SpecialType == SpecialType.System_String && (m.Parameters.Length == 1 || m.Parameters[1].Type.HasMetadataName(ClrNames.IFormatProvider) || m.Parameters[1].Type.HasMetadataName(ClrNames.CultureInfo)) &&
                _context.Types.Compilation.ClassifyCommonConversion(m.ReturnType, target).IsImplicit)
            .OrderByDescending(m => m.Parameters.Length).FirstOrDefault();
    private static BoundExpression? TryEnum(string text, INamedTypeSymbol type, TextSpan span)
    {
        if (type.EnumUnderlyingType is { } underlying && long.TryParse(text, out var number))
        {
            // Enum literals are signed 64-bit input; assignment keeps only the underlying bits.
            // Normalize before emission so constant casts also compile in a checked C# project.
            object value = underlying.SpecialType switch
            {
                SpecialType.System_Byte => (object)unchecked((byte)number),
                SpecialType.System_SByte => unchecked((sbyte)number),
                SpecialType.System_Int16 => unchecked((short)number),
                SpecialType.System_UInt16 => unchecked((ushort)number),
                SpecialType.System_Int32 => unchecked((int)number),
                SpecialType.System_UInt32 => unchecked((uint)number),
                SpecialType.System_UInt64 => unchecked((ulong)number),
                _ => number
            };
            return new BoundCastExpression(new BoundConstantExpression(value, underlying, span), type, span);
        }
        var flags = type.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "FlagsAttribute");
        var fields = ImmutableArray.CreateBuilder<IFieldSymbol>();
        foreach (var name in flags ? text.Split(',').Select(value => value.Trim()) : new[] { text })
        {
            var field = type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(candidate => candidate.HasConstantValue);
            if (field == null) return null;
            fields.Add(field);
        }
        return fields.Count == 0 ? null : new BoundEnumExpression(fields.ToImmutable(), type, span);
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
    public ITypeSymbol? ResolveTypeLiteral(string name, NamespaceScope scope, TextSpan span, string? typeArguments = null, bool report = true, NamespaceScope? typeArgumentScope = null)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = ResolveTypeLiteral(name.Substring(0, name.Length - 2), scope, span, typeArguments, report, typeArgumentScope);
            return element == null ? null : _context.Types.Compilation.CreateArrayTypeSymbol(element);
        }
        return _context.ResolveType(name, scope, span, typeArguments, report, typeArgumentScope: typeArgumentScope);
    }
    public BoundExpression? BindNode(XamlSyntaxNode syntax, ITypeSymbol target, NamespaceScope scope, int nameScope = 0, bool normalizeText = true, ISymbol? member = null)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        if (syntax is XamlTextSyntax text) return BindText(normalizeText ? XmlWhitespace.Normalize(text.Value, scope.PreserveSpace) : text.Value, target, scope, syntax.Span, member);
        if (syntax is not XamlElementSyntax element) return null;
        var nested = scope.Push(element); var name = nested.Expand(element.Name);
        foreach (var rule in _context.Profile.ObjectExpressionRules)
            if (rule.TryBind(_context, element, target, scope, nameScope, out var replacement))
                return replacement == null ? null : Coerce(replacement, target, element.Span, nested, member);
        if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
        {
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
            if (name.LocalName is "Type" or "Static" or "Reference" or "True" or "False" or "Null")
            {
                var arguments = element.Attributes.Where(attribute => !attribute.IsNamespace)
                    .Select(attribute => new MarkupArgumentSyntax(attribute.Name, attribute.Value, attribute.ValueSpan)).ToImmutableArray();
                var markup = new MarkupExtensionSyntax(element.Name, arguments, element.Span);
                foreach (var rule in _context.Profile.MarkupBindingRules)
                    if (rule.TryBind(_context, markup, target, nested, out var replacement))
                        return replacement == null ? null : Coerce(replacement, target, syntax.Span, nested, member);
                var value = new IntrinsicMarkupBinder(_context).BindObject(element, target, nested);
                return value == null ? null : Coerce(value, target, syntax.Span, nested, member);
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
                if (value != null) return Coerce(value, target, syntax.Span, nested, member);
            }
        }
        var bound = _context.Objects.Bind(element, scope, type, false, nameScope);
        if (bound == null) return null;
        var provided = _markup.Provide(bound, syntax.Span);
        return Coerce(provided ?? new BoundObjectExpression(bound), target, syntax.Span, nested, member);
    }
    public ITypeSymbol? PeekNodeType(XamlSyntaxNode syntax, NamespaceScope scope)
    {
        if (syntax is XamlTextSyntax) return _context.Types.Special(SpecialType.System_String);
        if (syntax is not XamlElementSyntax element) return null;
        var nested = scope.Push(element); var name = nested.Expand(element.Name);
        if (name.Namespace != null && XamlNames.IsLanguage(name.Namespace))
        {
            if (name.LocalName == "Null") return null;
            if (name.LocalName is "True" or "False") return _context.Types.Special(SpecialType.System_Boolean);
        }
        return _context.ResolveType(element.Name, nested, element.NameSpan, nested.Directive(element, "TypeArguments")?.Value, report: false);
    }
    public ITypeSymbol? PeekValueType(XamlSyntaxNode syntax, NamespaceScope scope, int nameScope = 0) => new ValueTypeProbe(_context).Peek(syntax, scope, nameScope);
    public bool TryGetStringLiteral(XamlSyntaxNode syntax, NamespaceScope scope, out string value)
    {
        if (syntax is XamlTextSyntax text) { value = text.Value; return true; }
        if (syntax is XamlElementSyntax element && PeekNodeType(element, scope)?.SpecialType == SpecialType.System_String)
        { value = string.Concat(element.Children.OfType<XamlTextSyntax>().Select(t => t.Value)); return true; }
        value = string.Empty; return false;
    }
    public BoundExpression? Coerce(BoundExpression value, ITypeSymbol target, TextSpan span, NamespaceScope? scope = null, ISymbol? member = null)
    {
        if (value.Type == null)
        {
            if (target.AcceptsNull()) return new BoundConstantExpression(null, target, span);
            _context.Report("XG1022", $"Null cannot be assigned to '{target}'.", span); return null;
        }
        var conversion = _context.Types.Compilation.ClassifyConversion(value.Type, target);
        if (conversion.IsImplicit && (!conversion.IsNumeric || _context.Types.Configuration.AllowImplicitNumericConversions)) return value;
        if (conversion.Exists && value.Type.SpecialType == SpecialType.System_Object) return new BoundCastExpression(value, target, span);
        if (TryConvert(value, target, scope ?? NamespaceScope.Empty, span, member) is { } converted) return converted;
        _context.Report("XG1023", $"Value of type '{value.Type}' cannot be assigned to '{target}'.", span); return null;
    }
}
