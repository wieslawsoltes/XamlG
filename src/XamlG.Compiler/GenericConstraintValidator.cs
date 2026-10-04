using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Compiler;

/// <summary>Checks the constraints which Roslyn's symbol Construct API does not validate.
/// Constraint substitution handles dependent parameters, nested generics and array arguments.</summary>
internal static class GenericConstraintValidator
{
    public static bool Validate(BindingContext context, INamedTypeSymbol definition,
        IReadOnlyList<ITypeSymbol> arguments, TextSpan span, bool report)
    {
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        for (var index = 0; index < definition.TypeParameters.Length; index++) substitutions.Add(definition.TypeParameters[index], arguments[index]);
        for (var index = 0; index < definition.TypeParameters.Length; index++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var parameter = definition.TypeParameters[index]; var argument = arguments[index];
            bool Fail(string constraint)
            {
                if (report) context.Report("XG1025", "Type argument '" + argument.ToDisplayString() + "' does not satisfy constraint '" + constraint + "' on '" + parameter.Name + "'.", span);
                return false;
            }
            var nullable = argument is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            if (argument.SpecialType == SpecialType.System_Void || argument.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer ||
                argument is INamedTypeSymbol { IsStatic: true }) return Fail("valid generic type argument");
            if (argument.IsRefLikeType && !parameter.AllowsRefLikeType) return Fail("non-ref-like type argument");
            if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType) return Fail("class");
            if (parameter.HasValueTypeConstraint && (!argument.IsValueType || nullable)) return Fail("struct");
            if (parameter.HasUnmanagedTypeConstraint && (!argument.IsUnmanagedType || nullable)) return Fail("unmanaged");
            if (parameter.HasConstructorConstraint && !CanConstruct(argument)) return Fail("new()");
            foreach (var constraint in parameter.ConstraintTypes)
            {
                var target = Substitute(context, constraint, substitutions);
                var conversion = context.Types.Compilation.ClassifyConversion(argument, target);
                // Numeric and user-defined implicit conversions never satisfy generic constraints.
                // Nullable<T> boxing does not prove that Nullable<T> implements T's interfaces.
                if (!conversion.IsIdentity && !(conversion.IsImplicit && conversion.IsReference) &&
                    !(conversion.IsBoxing && argument.IsValueType && !nullable)) return Fail(target.ToDisplayString());
            }
        }
        return true;
    }
    private static bool CanConstruct(ITypeSymbol argument)
    {
        if (argument is ITypeParameterSymbol parameter)
            return parameter.HasConstructorConstraint || parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint;
        if (argument is not INamedTypeSymbol type || type.IsAbstract) return false;
        var constructor = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        if (!type.IsValueType && constructor == null) return false;
        var required = type.Members().Any(m => m is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });
        return !required || constructor?.GetAttributes().Any(a => a.AttributeClass?.HasMetadataName("System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") == true) == true;
    }
    private static ITypeSymbol Substitute(BindingContext context, ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> substitutions)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (type is ITypeParameterSymbol parameter) return substitutions.TryGetValue(parameter, out var value) ? value : parameter;
        if (type is IArrayTypeSymbol array) return context.Types.Compilation.CreateArrayTypeSymbol(Substitute(context, array.ElementType, substitutions), array.Rank);
        if (type is not INamedTypeSymbol named) return type;
        var definition = named.OriginalDefinition;
        if (named.ContainingType != null)
        {
            var containing = (INamedTypeSymbol)Substitute(context, named.ContainingType, substitutions);
            definition = containing.GetTypeMembers(named.Name, named.Arity).FirstOrDefault(t =>
                SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, named.OriginalDefinition)) ?? definition;
        }
        return named.Arity == 0 ? definition : definition.Construct(named.TypeArguments.Select(t => Substitute(context, t, substitutions)).ToArray());
    }
}
