using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

public sealed class MemberBinder
{
    private readonly BindingContext _context;
    public MemberBinder(BindingContext context) => _context = context;
    public BoundMember? Resolve(ITypeSymbol target, string name, NamespaceScope scope, TextSpan span, string? valueHint = null, bool report = true)
    {
        var member = ResolveCore(target, name, scope, span, valueHint, report);
        if (member == null) return null;
        foreach (var rule in _context.Profile.MemberBindingRules) member = rule.Bind(_context, target, member, scope);
        return member;
    }
    private BoundMember? ResolveCore(ITypeSymbol target, string name, NamespaceScope scope, TextSpan span, string? valueHint, bool report)
    {
        var dot = name.LastIndexOf('.');
        INamedTypeSymbol? owner = null; var memberName = name;
        if (dot >= 0)
        {
            var ownerName = name.Substring(0, dot);
            var expandedOwner = scope.Expand(ownerName);
            for (var current = target as INamedTypeSymbol; current != null && owner == null; current = current.BaseType)
                if (current.Name == expandedOwner.LocalName && expandedOwner.Namespace != null &&
                    SymbolEqualityComparer.Default.Equals(_context.Types.Resolve(expandedOwner.Namespace, current.Name, current.Arity).Type, current.OriginalDefinition))
                    owner = current;
            owner ??= _context.ResolveType(ownerName, scope, span, report: report);
            if (owner == null) return null;
            memberName = name.Substring(dot + 1);
        }
        if (owner == null || _context.Types.Compilation.ClassifyCommonConversion(target, owner).IsImplicit)
        {
            var property = target.Members(memberName).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && !p.IsIndexer && _context.Types.IsAccessible(p, _context.RootClass));
            if (property != null)
            {
                _context.Symbols.Add(new(span, property, "property"));
                return new(memberName, BoundMemberKind.Property, property, property.Type,
                    property.GetMethod != null && _context.Types.IsAccessible(property.GetMethod, _context.RootClass) ? property.GetMethod : null,
                    property.SetMethod != null && _context.Types.IsAccessible(property.SetMethod, _context.RootClass) ? property.SetMethod : null, span);
            }
            var ev = target.Members(memberName).OfType<IEventSymbol>().FirstOrDefault(e => !e.IsStatic && _context.Types.IsAccessible(e, _context.RootClass));
            if (ev != null)
            { _context.Symbols.Add(new(span, ev, "event")); return new(memberName, BoundMemberKind.Event, ev, ev.Type, null, ev.AddMethod, span); }
        }
        if (owner != null)
        {
            var setters = owner.Members(ClrNames.SetPrefix + memberName).OfType<IMethodSymbol>().Where(m => m.IsStatic && m.Parameters.Length == 2 &&
                _context.Types.IsAccessible(m, _context.RootClass) && _context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit).ToArray();
            var setter = setters.OrderBy(m => SymbolEqualityComparer.Default.Equals(target, m.Parameters[0].Type) ? 0 : 1)
                .ThenBy(m => valueHint == null || _context.Values.TryText(valueHint, m.Parameters[1].Type, scope, span) != null ? 0 : 1).FirstOrDefault();
            var getter = owner.Members(ClrNames.GetPrefix + memberName).OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 1 &&
                _context.Types.IsAccessible(m, _context.RootClass) && _context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit);
            if (setter != null || getter != null)
            {
                var symbol = setter ?? getter!; _context.Symbols.Add(new(span, symbol, "attached-property"));
                return new(memberName, BoundMemberKind.AttachedProperty, symbol, setter?.Parameters[1].Type ?? getter!.ReturnType, getter, setter, span);
            }
            var addHandler = owner.Members("Add" + memberName + "Handler").OfType<IMethodSymbol>().FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 2 &&
                m.Parameters[1].Type.TypeKind == TypeKind.Delegate && _context.Types.IsAccessible(m) && _context.Types.Compilation.ClassifyCommonConversion(target, m.Parameters[0].Type).IsImplicit);
            if (addHandler != null) return new(memberName, BoundMemberKind.AttachedEvent, addHandler, addHandler.Parameters[1].Type, null, addHandler, span);
        }
        if (report) _context.Report("XG1005", $"Member '{name}' was not found or is inaccessible on '{target.ToDisplayString()}'.", span);
        return null;
    }
    public void BindAttribute(ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        var member = Resolve(target.Type, attribute.Name, scope, attribute.NameSpan, attribute.Value);
        if (member == null) return;
        if (member.Setter?.IsInitOnly == true && _context.Types.Find("System.Runtime.CompilerServices.UnsafeAccessorAttribute") == null)
        { _context.Report("XG1031", "Init-only Populate requires a target runtime with UnsafeAccessor support (.NET 8 or later).", attribute.NameSpan); return; }
        if (member.Kind is BoundMemberKind.Event or BoundMemberKind.AttachedEvent)
        { BindEventValue(target, member, new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan), scope); return; }
        foreach (var rule in _context.Profile.PropertyBindingRules)
            if (rule.TryBind(_context, target, member, ImmutableArray.Create<XamlSyntaxNode>(new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan)), scope, attribute.Span, true)) return;
        if (!member.CanWrite)
        {
            if (member.Getter != null && _context.Types.AddMethods(member.ValueType).Any())
            { BindCollectionItem(target, member, new XamlTextSyntax(attribute.Value, false, attribute.ValueSpan), scope, attribute: true); return; }
            _context.Report("XG1010", $"Property '{member.Name}' is read-only.", attribute.NameSpan); return;
        }
        var value = _context.Values.BindText(attribute.Value, member.ValueType, scope, attribute.ValueSpan, member.ConversionSource);
        if (value != null) AddSet(target, member, value, attribute.Span);
    }
    public void BindNodes(ObjectBindingBuilder target, BoundMember? member, IEnumerable<XamlSyntaxNode> children, NamespaceScope scope, TextSpan span)
    {
        var contentType = member?.ValueType ?? target.Type;
        var contentIsCollection = (member == null || member.Getter != null) && _context.Types.AddMethods(contentType).Any();
        var nodes = new ContentWhitespaceNormalizer(_context).Normalize(children, contentType, contentIsCollection, scope);
        if (member?.Kind is BoundMemberKind.Event or BoundMemberKind.AttachedEvent)
        {
            if (nodes.Length != 1) _context.Report("XG1017", "An event assignment requires exactly one handler value.", span);
            else BindEventValue(target, member, nodes[0], scope);
            return;
        }
        if (member != null)
            foreach (var rule in _context.Profile.PropertyBindingRules)
                if (rule.TryBind(_context, target, member, nodes.ToImmutableArray(), scope, span, false)) return;
        if (member?.Symbol.HasAttribute(_context.Types.Configuration.DeferredContentAttributes) == true)
        {
            var value = new DeferredContentBinder(_context).Bind(member, nodes, scope, span);
            if (value != null) AddSet(target, member, value, span);
            return;
        }
        if (member?.ValueType is IArrayTypeSymbol array && member.CanWrite && !(nodes.Length == 1 && nodes[0] is XamlElementSyntax e && e.LocalName == "Array"))
        {
            var values = ImmutableArray.CreateBuilder<BoundExpression>();
            foreach (var node in nodes) { var value = _context.Values.BindNode(node, array.ElementType, scope, target.NameScopeId); if (value != null) values.Add(value); }
            AddSet(target, member, new BoundArrayExpression(values.ToImmutable(), array, span), span); return;
        }
        var collectionType = member?.ValueType ?? target.Type;
        var isCollection = (member == null || member.Getter != null) && _context.Types.AddMethods(collectionType).Any();
        var firstValue = true;
        foreach (var node in nodes)
        {
            if (node is XamlElementSyntax ignored)
            {
                var nested = scope.Push(ignored); var ns = nested.Expand(ignored.Name).Namespace;
                if (ns != null && (nested.IgnoredNamespaces.Contains(ns) || _context.Types.Configuration.IgnoredNamespaces.Contains(ns))) continue;
            }
            var canReplace = firstValue;
            firstValue = false;
            if (isCollection)
            {
                if (member?.CanWrite == true && canReplace && new CollectionReplacementBinder(_context).TryBind(target, member, node, scope)) continue;
                var nodeType = _context.Values.PeekValueType(node, scope, target.NameScopeId);
                // The first value can replace the collection; later values add to that instance.
                if (member?.CanWrite == true && canReplace && nodeType != null && _context.Types.Compilation.ClassifyCommonConversion(nodeType, member.ValueType).IsImplicit)
                {
                    var collection = _context.Values.BindNode(node, member.ValueType, scope, target.NameScopeId);
                    if (collection != null) AddSet(target, member, collection, node.Span);
                }
                else BindCollectionItem(target, member, node, scope, true);
                continue;
            }
            if (member == null) { _context.Report("XG1011", $"'{target.Type}' has no content property or collection Add method.", node.Span); continue; }
            if (!member.CanWrite) { _context.Report("XG1010", $"Property '{member.Name}' is read-only.", span); continue; }
            var expression = _context.Values.BindNode(node, member.ValueType, scope, target.NameScopeId, normalizeText: false, member: member.ConversionSource);
            if (expression != null) AddSet(target, member, expression, node.Span);
        }
    }
    public void AddSet(ObjectBindingBuilder target, BoundMember member, BoundExpression value, TextSpan span)
    {
        var key = member.Symbol.ToDisplayString();
        if (!target.AssignedScalars.Add(key)) { _context.Report("XG1014", $"Property '{member.Name}' is assigned more than once.", span); return; }
        target.Assignments.Add(new BoundSetAssignment(member, value, span));
    }
    private void BindCollectionItem(ObjectBindingBuilder target, BoundMember? member, XamlSyntaxNode syntax, NamespaceScope scope, bool normalized = false, bool attribute = false)
    {
        var type = member?.ValueType ?? target.Type;
        var elementScope = syntax is XamlElementSyntax element ? scope.Push(element) : scope;
        var key = syntax is XamlElementSyntax keyed ? elementScope.Directive(keyed, "Key") : null;
        var methods = _context.Types.AddMethods(type).Where(m => m.Parameters.Length == (key == null ? 1 : 2)).ToArray();
        if (!new CollectionKeyBinder(_context).TryBind(key, elementScope, ref methods, out var boundKey)) return;
        if (new DynamicCollectionBinder(_context).TryBind(target, member, syntax, scope, boundKey, methods, attribute)) return;
        var nodeType = _context.Values.PeekValueType(syntax, scope, target.NameScopeId);
        var isLiteral = _context.Values.TryGetStringLiteral(syntax, scope, out var literal);
        if (syntax is XamlTextSyntax && isLiteral)
        {
            if (!normalized) literal = XmlWhitespace.Normalize(literal, scope.PreserveSpace);
            if (literal.StartsWith("{}", StringComparison.Ordinal)) literal = literal.Substring(2);
        }
        IMethodSymbol? selected = null;
        BoundExpression? converted = null;
        BoundExpression? provided = null;
        if (!isLiteral && nodeType?.SpecialType == SpecialType.System_String)
        {
            provided = _context.Values.BindNode(syntax, _context.Types.Special(SpecialType.System_Object), scope, target.NameScopeId);
            if (provided == null) return;
        }
        foreach (var method in methods)
        {
            var parameter = method.Parameters[method.Parameters.Length - 1].Type;
            var conversion = nodeType == null ? default : _context.Types.Compilation.ClassifyConversion(nodeType, parameter);
            if (!attribute && (nodeType == null ? parameter.AcceptsNull() : conversion.IsImplicit && (!conversion.IsNumeric || _context.Types.Configuration.AllowImplicitNumericConversions)))
            {
                selected ??= method;
                continue;
            }
            var input = isLiteral ? new BoundConstantExpression(literal, _context.Types.Special(SpecialType.System_String), syntax.Span) : provided;
            if (input != null && _context.Values.TryConvert(input, parameter, elementScope, syntax.Span) is { } value)
            {
                selected = method;
                converted = value;
                break;
            }
            if (isLiteral && PrimitiveValueParser.IsScalar(parameter))
            { _context.Report("XG1008", $"Cannot convert '{literal}' to '{parameter.ToDisplayString()}'.", syntax.Span); return; }
        }
        if (selected == null)
        {
            if (attribute) { _context.Report("XG1010", $"Property '{member!.Name}' is read-only and no collection adder converts the attribute value.", syntax.Span); return; }
            var hasDictionary = _context.Types.AddMethods(type).Any(m => m.Parameters.Length == 2);
            _context.Report("XG1016", key == null && hasDictionary ? "Dictionary entries require x:Key." : $"No compatible Add method on '{type}'.", syntax.Span); return;
        }
        var args = ImmutableArray.CreateBuilder<BoundExpression>();
        if (boundKey != null) args.Add(boundKey);
        var boundValue = converted ?? (provided == null
            ? _context.Values.BindNode(syntax, selected.Parameters[selected.Parameters.Length - 1].Type, scope, target.NameScopeId, normalizeText: !normalized)
            : _context.Values.Coerce(provided, selected.Parameters[selected.Parameters.Length - 1].Type, syntax.Span, elementScope));
        if (boundValue == null) return; args.Add(boundValue);
        target.Assignments.Add(new BoundAddAssignment(member, selected, args.ToImmutable(), syntax.Span));
    }
    private void BindEventValue(ObjectBindingBuilder target, BoundMember member, XamlSyntaxNode syntax, NamespaceScope scope)
    {
        if (_context.Values.TryGetStringLiteral(syntax, scope, out var name) && !name.StartsWith("{", StringComparison.Ordinal))
        { BindEvent(target, member, name, syntax.Span); return; }
        var value = _context.Values.BindNode(syntax, member.ValueType, scope, target.NameScopeId, normalizeText: false, member: member.ConversionSource);
        if (value != null) target.Assignments.Add(new BoundEventAssignment(member, string.Empty, null, syntax.Span) { Value = value });
    }
    private void BindEvent(ObjectBindingBuilder target, BoundMember member, string handlerName, TextSpan span)
    {
        if (!SyntaxFacts.IsValidIdentifier(handlerName) && !(handlerName.StartsWith("@", StringComparison.Ordinal) && SyntaxFacts.IsValidIdentifier(handlerName.Substring(1))))
        { _context.Report("XG1017", "An event handler must be a method identifier, not an expression.", span); return; }
        var handler = member.ValueType is INamedTypeSymbol delegateType ? RootMethodBinder.Resolve(_context, handlerName, delegateType) : null;
        if (handler == null) { _context.Report("XG1017", $"Compatible code-behind handler '{handlerName}' was not found.", span); return; }
        _context.Symbols.Add(new(span, handler, "event-handler"));
        target.Assignments.Add(new BoundEventAssignment(member, handlerName.TrimStart('@'), handler, span));
    }
}
