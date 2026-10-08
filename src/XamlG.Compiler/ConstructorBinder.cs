using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;
internal sealed class ConstructorBinder
{
    private readonly BindingContext _context;
    public ConstructorBinder(BindingContext context) => _context = context;
    public void Bind(ObjectBindingBuilder target, ImmutableArray<XamlSyntaxNode> arguments, NamespaceScope scope)
    {
        var factoryName = target.Scope.Directive(target.Syntax, "FactoryMethod")?.Value;
        if (factoryName == null && arguments.IsEmpty)
        {
            // An accessible parameterless constructor wins before optional overloads.
            // Most construction nodes take this path and need no conversion ranking.
            foreach (var constructor in target.Type.InstanceConstructors)
                if (constructor.Parameters.IsEmpty && _context.Types.IsAccessible(constructor, _context.RootClass))
                {
                    target.Constructor = constructor;
                    target.Arguments = ImmutableArray<BoundExpression>.Empty;
                    _context.Symbols.Add(new(target.Syntax.NameSpan, constructor, "constructor"));
                    return;
                }
        }
        IEnumerable<IMethodSymbol> candidates = factoryName == null ? target.Type.InstanceConstructors :
            target.Type.Members(factoryName).OfType<IMethodSymbol>().Where(m => m.IsStatic && _context.Types.Compilation.ClassifyCommonConversion(m.ReturnType, target.Type).IsImplicit);
        var exact = candidates.Where(method => _context.Types.IsAccessible(method, _context.RootClass) && !method.IsGenericMethod && method.Parameters.Length == arguments.Length).ToArray();
        var argumentTypes = arguments.Select(argument => _context.Values.PeekValueType(argument, scope, target.NameScopeId)).ToArray();
        var preferred = exact.FirstOrDefault(method => method.Parameters.Select((parameter, index) => CanAssign(argumentTypes[index], parameter.Type)).All(value => value))
            ?? exact.FirstOrDefault();
        if (preferred != null) candidates = new[] { preferred };
        var ranked = new List<(IMethodSymbol Method, int Score)>();
        foreach (var method in candidates.Where(m => _context.Types.IsAccessible(m, _context.RootClass) && !m.IsGenericMethod))
        {
            if (SymbolEqualityComparer.Default.Equals(method, preferred)) { ranked.Add((method, 0)); continue; }
            if (arguments.Length > method.Parameters.Length || method.Parameters.Skip(arguments.Length).Any(p => !p.IsOptional)) continue;
            var score = method.Parameters.Length - arguments.Length; var valid = true;
            for (var i = 0; i < arguments.Length; i++)
            {
                var argument = arguments[i]; var parameter = method.Parameters[i].Type;
                if (argument is XamlTextSyntax text)
                {
                    if (text.Value.StartsWith("{", StringComparison.Ordinal)) { score += 10; continue; }
                    if (_context.Values.TryText(text.Value, parameter, scope, text.Span, method.Parameters[i]) == null) { valid = false; break; }
                    if (parameter.SpecialType == SpecialType.System_Object) score += 3;
                    else if (parameter.SpecialType != SpecialType.System_String) score++;
                }
                else
                {
                    var type = _context.Values.PeekValueType(argument, scope, target.NameScopeId);
                    if (type == null && !parameter.AcceptsNull()) { valid = false; break; }
                    if (type != null && !_context.Types.Compilation.ClassifyCommonConversion(type, parameter).IsImplicit)
                    {
                        if (type.SpecialType == SpecialType.System_Object && _context.Types.Compilation.ClassifyCommonConversion(type, parameter).Exists) score += 5;
                        else if (_context.Values.TryGetStringLiteral(argument, scope, out var literal)
                            ? _context.Values.TryText(literal, parameter, argument is XamlElementSyntax element ? scope.Push(element) : scope,
                                argument.Span, method.Parameters[i]) == null
                            : !_context.Values.CanConvertValueType(type, parameter, method.Parameters[i])) { valid = false; break; }
                        score += 2;
                    }
                    if (!SymbolEqualityComparer.Default.Equals(type, parameter)) score++;
                }
            }
            if (valid) ranked.Add((method, score));
        }
        if (ranked.Count == 0)
        {
            // Service-provider constructors are a common XAML convention; no framework name is required.
            var serviceConstructor = factoryName == null && arguments.Length == 0 ? target.Type.InstanceConstructors.FirstOrDefault(c =>
                c.Parameters.Length == 1 && c.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider) && _context.Types.IsAccessible(c, _context.RootClass)) : null;
            if (serviceConstructor != null)
            {
                target.Constructor = serviceConstructor;
                target.Arguments = ImmutableArray.Create<BoundExpression>(new BoundServiceExpression(serviceConstructor.Parameters[0].Type, target.Syntax.Span)); return;
            }
            if (target.IsRoot && (_context.RootClass != null || !_context.Options.GenerateBuildMethod)) return; // Populate existing roots need no public constructor.
            _context.Report("XG1006", $"No accessible {(factoryName == null ? "constructor" : "factory method")} for '{target.Type}' accepts {arguments.Length} argument(s).", target.Syntax.NameSpan); return;
        }
        var sorted = ranked.OrderBy(c => c.Score).ToArray();
        if (sorted.Length > 1 && sorted[0].Score == sorted[1].Score)
        { _context.Report("XG1007", $"Ambiguous constructor or factory for '{target.Type}'.", target.Syntax.NameSpan); return; }
        var selected = sorted[0].Method;
        if (factoryName == null) target.Constructor = selected; else target.FactoryMethod = selected;
        var values = ImmutableArray.CreateBuilder<BoundExpression>();
        for (var i = 0; i < selected.Parameters.Length; i++)
        {
            var parameter = selected.Parameters[i];
            if (i < arguments.Length && argumentTypes[i]?.SpecialType == SpecialType.System_Object && !CanAssign(argumentTypes[i], parameter.Type) &&
                !_context.Values.CanConvertValueType(argumentTypes[i]!, parameter.Type, parameter))
            { _context.Report("XG1023", $"An object-valued constructor argument cannot be downcast to '{parameter.Type}'.", arguments[i].Span); continue; }
            var value = i < arguments.Length ? _context.Values.BindNode(arguments[i], parameter.Type, scope, target.NameScopeId, member: parameter) :
                new BoundConstantExpression(parameter.ExplicitDefaultValue, parameter.Type, target.Syntax.NameSpan);
            if (value != null) values.Add(value);
        }
        target.Arguments = values.ToImmutable();
        _context.Symbols.Add(new(target.Syntax.NameSpan, selected, factoryName == null ? "constructor" : "factory"));
    }
    private bool CanAssign(ITypeSymbol? source, ITypeSymbol target)
    {
        if (source == null) return target.AcceptsNull();
        var conversion = _context.Types.Compilation.ClassifyConversion(source, target);
        return conversion.IsImplicit && !conversion.IsUserDefined && (!conversion.IsNumeric || _context.Types.Configuration.AllowImplicitNumericConversions);
    }
}
