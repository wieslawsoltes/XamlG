using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.Compiler;

internal sealed class DeferredContentBinder
{
    private readonly BindingContext _context;
    public DeferredContentBinder(BindingContext context) => _context = context;
    public BoundExpression? Bind(BoundMember member, XamlSyntaxNode[] nodes, NamespaceScope scope, TextSpan span)
    {
        if (nodes.Length != 1) { _context.Report("XG1013", "Deferred content requires exactly one root value.", span); return null; }
        var customizer = _context.Runtime.DeferredContentCustomizer;
        if (customizer?.IsGenericMethod == true)
        {
            if (customizer.Arity != 1)
            { _context.Report("XG1013", "A generic deferred-content customizer must have one type parameter.", span); return null; }
            ITypeSymbol? argument = _context.Profile.Runtime.DeferredDefaultTypeArgument is { } name ? _context.Types.Find(name) : null;
            foreach (var attribute in member.Symbol.GetAttributes().Where(a => a.AttributeClass != null && _context.Types.Configuration.DeferredContentAttributes.Contains(a.AttributeClass.ToDisplayString())))
                foreach (var pair in attribute.NamedArguments)
                    if (_context.Profile.Runtime.DeferredTypeArgumentAttributeProperties.Contains(pair.Key) && pair.Value.Value is ITypeSymbol type) argument = type;
            if (argument == null)
            { _context.Report("XG1013", "A generic deferred-content customizer requires a resolved default or attribute type argument.", span); return null; }
            customizer = customizer.Construct(argument);
        }
        var delegateType = customizer?.Parameters[0].Type ?? member.ValueType;
        var invoke = (delegateType as INamedTypeSymbol)?.DelegateInvokeMethod;
        var pointer = delegateType.SpecialType == SpecialType.System_IntPtr;
        if (customizer != null && (!customizer.Parameters[1].Type.HasMetadataName(ClrNames.IServiceProvider) ||
            !_context.Types.Compilation.ClassifyCommonConversion(customizer.ReturnType, member.ValueType).IsImplicit || !pointer && invoke == null))
        { _context.Report("XG1013", "The deferred customizer must accept a builder and IServiceProvider, and return a value assignable to the target property.", span); return null; }
        if (invoke != null && (invoke.Parameters.Length != 1 || !invoke.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider)))
        { _context.Report("XG1013", "Deferred content delegates must accept one IServiceProvider.", span); return null; }
        var returnType = invoke?.ReturnType ?? _context.Types.Special(SpecialType.System_Object);
        var nameScope = _context.NewNameScope();
        var value = _context.Values.BindNode(nodes[0], returnType, scope, nameScope);
        return value == null ? null : new BoundDeferredExpression(value, member.ValueType, span)
        { Customizer = customizer, FactoryReturnType = returnType, NameScopeId = nameScope };
    }
}
