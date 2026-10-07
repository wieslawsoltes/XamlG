using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Roslyn;

namespace XamlG.Compiler;

/// <summary>Resolves root instance methods for delegate values and event handlers without
/// admitting numeric, boxing or user-defined conversions into method-group conversions.</summary>
internal static class RootMethodBinder
{
    public static IMethodSymbol? Resolve(BindingContext context, string name, INamedTypeSymbol delegateType)
    {
        if (!SyntaxFacts.IsValidIdentifier(name) && !(name.StartsWith("@", StringComparison.Ordinal) && SyntaxFacts.IsValidIdentifier(name.Substring(1)))) return null;
        var root = context.RootClass ?? context.Ancestors.LastOrDefault()?.Type;
        var invoke = delegateType.DelegateInvokeMethod;
        if (root == null || invoke == null) return null;
        var within = context.RootClass != null && XamlClassAugmentation.IsAvailable(context.RootClass, context.Cancellation)
            ? context.RootClass : null;
        var candidates = new List<IMethodSymbol>();
        foreach (var method in root.Members(name.TrimStart('@')).OfType<IMethodSymbol>())
        {
            if (method.IsStatic || method.IsGenericMethod || method.MethodKind != MethodKind.Ordinary ||
                !context.Types.IsAccessible(method, within) || method.Parameters.Length != invoke.Parameters.Length || method.RefKind != invoke.RefKind) continue;
            if (!method.Parameters.Select((parameter, index) => parameter.RefKind == invoke.Parameters[index].RefKind &&
                Compatible(context, invoke.Parameters[index].Type, parameter.Type, parameter.RefKind != RefKind.None)).All(value => value)) continue;
            if (!(invoke.ReturnsVoid ? method.ReturnsVoid : Compatible(context, method.ReturnType, invoke.ReturnType, invoke.RefKind != RefKind.None))) continue;
            // Symbols are enumerated from the most-derived declaration first.
            if (!candidates.Any(candidate => SameParameters(candidate, method))) candidates.Add(method);
        }
        return candidates.FirstOrDefault(candidate => candidates.All(other =>
            ReferenceEquals(candidate, other) || MoreSpecific(context, candidate, other)));
    }

    private static bool Compatible(BindingContext context, ITypeSymbol source, ITypeSymbol target, bool byReference)
    {
        var conversion = context.Types.Compilation.ClassifyConversion(source, target);
        return conversion.IsIdentity || !byReference && conversion.IsImplicit && conversion.IsReference;
    }

    private static bool SameParameters(IMethodSymbol first, IMethodSymbol second) =>
        first.Parameters.Select((parameter, index) => parameter.RefKind == second.Parameters[index].RefKind &&
            SymbolEqualityComparer.Default.Equals(parameter.Type, second.Parameters[index].Type)).All(value => value);

    private static bool MoreSpecific(BindingContext context, IMethodSymbol first, IMethodSymbol second) =>
        first.Parameters.Select((parameter, index) => Compatible(context, parameter.Type, second.Parameters[index].Type,
            parameter.RefKind != RefKind.None)).All(value => value);
}
