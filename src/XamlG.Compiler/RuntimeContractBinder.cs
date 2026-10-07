using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Roslyn;

namespace XamlG.Compiler;

internal static class RuntimeContractBinder
{
    public static BoundRuntimeConfiguration Bind(BindingContext context)
    {
        var services = ImmutableArray.CreateBuilder<BoundServiceContract>();
        foreach (var mapping in context.Profile.Runtime.Services)
        {
            var type = context.Types.Find(mapping.InterfaceMetadataName);
            if (type == null || type.TypeKind != TypeKind.Interface || !context.Types.IsAccessible(type))
            {
                Error(context, $"Service contract '{mapping.InterfaceMetadataName}' must resolve to an accessible interface.");
                continue;
            }

            var properties = ImmutableArray.CreateBuilder<BoundServiceProperty>();
            foreach (var member in type.Members())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }) continue;
                if (member is not IPropertySymbol property || property.IsIndexer || property.IsStatic || property.GetMethod == null)
                {
                    Error(context, $"Service contract member '{member}' must be a readable instance property.");
                    continue;
                }
                var value = mapping.GetValue(property.Name);
                if (value == null)
                {
                    Error(context, $"Service property '{property}' has no configured value mapping.");
                    continue;
                }
                if (property.SetMethod != null && value != XamlServiceValue.BaseUri)
                    Error(context, $"Only URI-context service properties may have a setter: '{property}'.");
                properties.Add(new(property, value.Value));
            }

            INamedTypeSymbol? namespaceItem = mapping.NamespaceItemMetadataName == null ? null : context.Types.Find(mapping.NamespaceItemMetadataName);
            IPropertySymbol? namespaceName = null;
            IPropertySymbol? assemblyName = null;
            var namespaceProperty = properties.FirstOrDefault(p => p.Value == XamlServiceValue.XmlNamespaces)?.Property;
            if (namespaceProperty != null)
            {
                if (namespaceProperty.Type is INamedTypeSymbol { TypeArguments.Length: 2 } dictionary &&
                    dictionary.TypeArguments[1] is INamedTypeSymbol { TypeArguments.Length: 1 } list)
                    namespaceItem ??= list.TypeArguments[0] as INamedTypeSymbol;
                if (namespaceItem == null || !context.Types.IsAccessible(namespaceItem) ||
                    !namespaceItem.InstanceConstructors.Any(c => c.Parameters.Length == 0 && context.Types.IsAccessible(c)))
                    Error(context, $"Namespace information for '{type}' requires an accessible parameterless item type.");
                else
                {
                    namespaceName = FindWritableString(namespaceItem, mapping.NamespaceNameProperty, context);
                    assemblyName = FindWritableString(namespaceItem, mapping.AssemblyNameProperty, context);
                }
            }
            services.Add(new(mapping, type, namespaceItem)
            {
                Properties = properties.ToImmutable(),
                NamespaceNameProperty = namespaceName,
                AssemblyNameProperty = assemblyName
            });
        }

        return new(services.ToImmutable(),
            Resolve(context, context.Profile.Runtime.InnerServiceProviderFactory, 1),
            Resolve(context, context.Profile.Runtime.DeferredContentCustomizer, 2),
            RuntimeNamespaceResolver.Collect(context), context.Types.Compilation.AssemblyName ?? string.Empty)
        {
            RootServiceProviderFactory = Resolve(context, context.Profile.Runtime.RootServiceProviderFactory, 1),
            NameScope = BindNameScope(context),
            SourceInfo = BindSourceInfo(context)
        };
    }

    private static BoundSourceInfo? BindSourceInfo(BindingContext context)
    {
        if (context.Profile.Runtime.SourceInfo is not { } configuration) return null;
        var type = context.Types.Find(configuration.TypeMetadataName);
        var constructor = type is { IsAbstract: false } && context.Types.IsAccessible(type) ? type.InstanceConstructors.FirstOrDefault(method =>
            context.Types.IsAccessible(method) && method.Parameters.All(parameter => parameter.RefKind == RefKind.None) && method.Parameters.Length == 3 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_Int32 && method.Parameters[1].Type.SpecialType == SpecialType.System_Int32 &&
            method.Parameters[2].Type.SpecialType == SpecialType.System_String) : null;
        var setter = type?.GetMembers(configuration.SetterMethod).OfType<IMethodSymbol>().FirstOrDefault(method =>
            method.IsStatic && !method.IsGenericMethod && method.ReturnsVoid && context.Types.IsAccessible(method) &&
            method.Parameters.All(parameter => parameter.RefKind == RefKind.None) && method.Parameters.Length == 2 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_Object && SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, type));
        if (constructor != null && setter != null) return new(constructor, setter);
        Error(context, "Source information requires an accessible (int, int, string) constructor and static (object, metadata) setter.");
        return null;
    }

    private static IPropertySymbol? FindWritableString(INamedTypeSymbol type, string name, BindingContext context)
    {
        var property = type.Members(name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic &&
            p.Type.SpecialType == SpecialType.System_String && p.SetMethod != null && context.Types.IsAccessible(p.SetMethod));
        if (property == null) Error(context, $"Namespace information member '{type}.{name}' must be a writable string property.");
        return property;
    }

    private static BoundNameScopeIntegration? BindNameScope(BindingContext context)
    {
        var configuration = context.Profile.Runtime.NameScope;
        if (configuration == null) return null;
        var concrete = context.Types.Find(configuration.ConcreteMetadataName);
        var contract = context.Types.Find(configuration.ContractMetadataName);
        if (concrete == null || contract == null || !context.Types.IsAccessible(concrete) ||
            !concrete.InstanceConstructors.Any(c => c.Parameters.Length == 0 && context.Types.IsAccessible(c)) ||
            !context.Types.Compilation.ClassifyCommonConversion(concrete, contract).IsImplicit)
        {
            Error(context, "The configured name-scope implementation must be accessible, constructible, and implement its contract.");
            return null;
        }
        var register = contract.Members(configuration.RegisterMethod).OfType<IMethodSymbol>().FirstOrDefault(m =>
            !m.IsStatic && m.Parameters.Length == 2 && m.Parameters[0].Type.SpecialType == SpecialType.System_String && m.Parameters[1].Type.SpecialType == SpecialType.System_Object);
        var complete = contract.Members(configuration.CompleteMethod).OfType<IMethodSymbol>().FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0);
        if (register == null || complete == null)
        {
            Error(context, "The configured name scope requires Register(string, object) and Complete() operations.");
            return null;
        }
        return new(concrete, contract, register, complete, Resolve(context, configuration.Attach, 2));
    }

    private static IMethodSymbol? Resolve(BindingContext context, XamlMethodReference? reference, int parameterCount)
    {
        if (reference == null) return null;
        var methods = context.Types.Find(reference.TypeMetadataName)?.Members(reference.MethodName).OfType<IMethodSymbol>()
            .Where(m => m.IsStatic && m.Parameters.Length == parameterCount && context.Types.IsAccessible(m)).ToArray()
            ?? Array.Empty<IMethodSymbol>();
        if (methods.Length == 1) return methods[0];
        context.Report("XG1101", $"Runtime method '{reference.TypeMetadataName}.{reference.MethodName}' is missing or ambiguous.", new(0, 0));
        return null;
    }

    private static void Error(BindingContext context, string message) => context.Report("XG1100", message, new(0, 0));
}
