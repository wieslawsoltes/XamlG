using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Resources;

/// <summary>Uses public resource factories for reference values without names in the current scope.</summary>
public sealed class AvaloniaDeferredResourceRule : IXamlObjectBindingRule, IXamlBindingRule, IXamlObjectExpressionRule
{
    private static readonly XamlAnnotationKey<Dictionary<XamlElementSyntax, SharedDirective>> SharedKey = new("Avalonia.SharedResources");
    private sealed class SharedDirective(string value, TextSpan span)
    {
        public string Value { get; } = value;
        public TextSpan Span { get; } = span;
        public bool Used { get; set; }
    }

    public void Initialize(BindingContext context, ObjectBindingBuilder target) { }

    public bool TryBind(BindingContext context, XamlElementSyntax syntax, ITypeSymbol targetType,
        NamespaceScope parentScope, int nameScope, out BoundExpression? expression)
    {
        expression = null;
        if (parentScope.Push(syntax).Directive(syntax, "Shared") is { } directive)
            Shared(context)[syntax] = new(directive.Value, directive.ValueSpan);
        return false;
    }

    public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
    {
        var name = scope.Expand(attribute.Name, true);
        if (name.Namespace == null || !XamlNames.IsLanguage(name.Namespace) || name.LocalName != "Shared") return false;
        Shared(context)[target.Syntax] = new(attribute.Value, attribute.ValueSpan);
        return true;
    }

    public void Complete(BindingContext context, ObjectBindingBuilder target)
    {
        var dictionary = context.Types.Find(AvaloniaResourceMetadata.Dictionary);
        var isDictionary = dictionary != null && context.Types.Compilation.ClassifyCommonConversion(target.Type, dictionary).IsImplicit;
        for (var index = 0; index < target.Assignments.Count; index++)
        {
            var assignment = target.Assignments[index];
            var add = assignment as BoundAddAssignment;
            var call = assignment as BoundCallAssignment;
            var merged = call?.Method.ContainingType.HasMetadataName(AvaloniaResourceMetadata.Operations) == true && call.Method.Name == AvaloniaResourceMetadata.SetResource;
            var resource = add?.Arguments.Length == 2 && (isDictionary && add.Collection == null ||
                add.Collection?.Name == "Resources" && add.Collection.Getter?.ReturnType.HasMetadataName(AvaloniaResourceMetadata.DictionaryContract) == true);
            if (!resource && !merged) continue;
            var arguments = add?.Arguments ?? call!.Arguments;
            var value = arguments[1];
            if (value.Type?.IsValueType == true || value.Type?.SpecialType == SpecialType.System_String || AvaloniaResourceNames.Contains(value)) continue;

            var notShared = false;
            if (value is BoundObjectExpression obj && Shared(context).TryGetValue(obj.Object.Syntax, out var shared))
            {
                shared.Used = true;
                if (!bool.TryParse(shared.Value, out _))
                    context.Report("XG3310", "x:Shared requires True or False.", shared.Span);
                // The pinned transform consumes either Boolean but leaves its out value false.
                notShared = true;
            }
            var contract = context.Types.Find(AvaloniaResourceMetadata.DeferredContent);
            var customizer = context.Types.Find(AvaloniaResourceMetadata.DeferredFactory)?.GetMembers("Create").OfType<IMethodSymbol>()
                .SingleOrDefault(method => method.IsStatic && method.Parameters.Length == 2 &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_IntPtr &&
                    method.Parameters[1].Type.HasMetadataName(ClrNames.IServiceProvider) &&
                    SymbolEqualityComparer.Default.Equals(method.ReturnType, contract));
            var methodName = notShared ? "AddNotSharedDeferred" : "AddDeferred";
            var adder = dictionary?.GetMembers(methodName).OfType<IMethodSymbol>().SingleOrDefault(method =>
                method.Parameters.Length == 2 && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, contract));
            if (contract == null || customizer == null || adder == null)
            { context.Report("XG3308", "Deferred resources require the public deferred-content contracts.", value.Span); continue; }
            var deferred = new BoundDeferredExpression(value, contract, value.Span)
            {
                Customizer = customizer, FactoryReturnType = context.Types.Special(SpecialType.System_Object),
                NameScopeId = context.NewNameScope()
            };
            arguments = arguments.SetItem(1, deferred);
            if (add != null)
                target.Assignments[index] = add with { AddMethod = adder, Arguments = arguments, Alternatives = ImmutableArray<IMethodSymbol>.Empty };
            else
            {
                var method = notShared ? context.Types.Find(AvaloniaResourceMetadata.Operations)?.GetMembers(AvaloniaResourceMetadata.SetNotSharedDeferredResource).OfType<IMethodSymbol>().SingleOrDefault() : call!.Method;
                if (method == null) { context.Report("XG3308", "Non-shared merged resources require the matching runtime contract.", value.Span); continue; }
                target.Assignments[index] = call! with { Method = method, Arguments = arguments };
            }
        }
        if (target.IsRoot)
            foreach (var directive in Shared(context).Values.Where(value => !value.Used))
                context.Report("XG3310", "x:Shared is supported only on deferred resource objects.", directive.Span);
    }

    private static Dictionary<XamlElementSyntax, SharedDirective> Shared(BindingContext context)
    {
        var root = context.Ancestors.Last();
        if (!root.Annotations.TryGet(SharedKey, out var shared)) root.Annotations.Set(SharedKey, shared = new());
        return shared;
    }
}
