using System.Collections.Immutable;
using System.Threading;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Indexes local root signatures and referenced export metadata without compiling or executing a factory.</summary>
public static class XamlResourceCatalogBuilder
{
    private static readonly ConditionalWeakTable<RoslynTypeSystem, Exports> ReferencedExports = new();
    private sealed record Exports(ImmutableArray<XamlResourceDescriptor> Resources);
    public static XamlResourceCatalog Create(IReadOnlyList<XamlProjectDocument> documents, RoslynTypeSystem types,
        XamlFrameworkProfile profile, XamlCompilerOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resources = new List<XamlResourceDescriptor>();
        foreach (var input in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = input.Syntax.Root;
            if (root == null || !options.GenerateBuildMethod || !profile.Directives.ShouldCompile(input.Syntax, options)) continue;
            var scope = NamespaceScope.Empty.Push(root);
            var classDirective = scope.Directive(root, "Class");
            var classType = classDirective == null ? null : types.Find(classDirective.Value);
            if (classDirective != null && (classType == null || !types.IsAccessible(classType) || !XamlClassFactory.CanCreate(classType, options, root))) continue;
            string uri;
            try { uri = Address(input, types, profile); }
            catch (ArgumentException) { continue; }
            // Custom rules can observe eager runtime diagnostics before reading
            // Runtime. Preserve their historical context; only the built-in path
            // may defer the contract and document namespace walk.
            var context = profile.TypeBindingRules.IsDefaultOrEmpty
                ? BindingContext.CreateSignatureProbe(input.Syntax, types, profile, options, cancellationToken)
                : new BindingContext(input.Syntax, types, profile, options, cancellationToken);
            var typeArguments = scope.Directive(root, "TypeArguments");
            var type = context.ResolveTypeAtSource(root.Name, scope, root.NameSpan, typeArguments?.Value, report: false, typeArgumentSpan: typeArguments?.ValueSpan);
            if (type != null)
            {
                var localClass = classType != null && XamlClassAugmentation.IsAvailable(classType, cancellationToken) ? classType : null;
                resources.Add(new(uri, classType ?? type, input.LogicalPath, options.GeneratedNamespace, null)
                {
                    LocalFactoryType = localClass,
                    LocalFactoryMethod = localClass == null ? null : XamlClassFactory.Method(input.LogicalPath)
                });
            }
        }
        if (!ReferencedExports.TryGetValue(types, out var exports))
            exports = ReferencedExports.GetValue(types, owner => ReadExports(owner, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        resources.AddRange(exports.Resources);
        return new(resources);
    }

    private static Exports ReadExports(RoslynTypeSystem types, CancellationToken cancellationToken)
    {
        var resources = ImmutableArray.CreateBuilder<XamlResourceDescriptor>();
        foreach (var assembly in types.Compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var attribute in assembly.GetAttributes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attribute.AttributeClass?.HasMetadataName(XamlResourceMetadata.ExportAttribute) != true || attribute.ConstructorArguments.Length != 3) continue;
                if (attribute.ConstructorArguments[0].Value is not string uri ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol factory ||
                    attribute.ConstructorArguments[2].Value is not string name) continue;
                var methods = factory.GetMembers(name).OfType<IMethodSymbol>().Where(m => m.IsStatic && m.MethodKind == MethodKind.Ordinary && !m.IsGenericMethod && !m.ReturnsByRef && !m.ReturnsByRefReadonly &&
                    m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.None && m.Parameters[0].Type.HasMetadataName(XamlResourceMetadata.ServiceProvider) &&
                    m.ReturnType is INamedTypeSymbol && !m.ReturnsVoid && types.IsAccessible(m)).ToArray();
                if (methods.Length != 1 || !types.IsAccessible(factory) || HasUnboundParameters(factory)) continue;
                try { uri = XamlResourceUri.Normalize(uri); }
                catch (ArgumentException) { continue; }
                resources.Add(new(uri, (INamedTypeSymbol)methods[0].ReturnType, null, string.Empty, methods[0]));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(resources.ToImmutable());
    }
    private static bool HasUnboundParameters(INamedTypeSymbol type)
    {
        if (type.IsUnboundGenericType || type.TypeArguments.Any(argument => argument is ITypeParameterSymbol || argument is INamedTypeSymbol nested && HasUnboundParameters(nested))) return true;
        return type.ContainingType != null && HasUnboundParameters(type.ContainingType);
    }
    public static string Address(XamlProjectDocument input, RoslynTypeSystem types, XamlFrameworkProfile profile) => input.ResourceUri == null
        ? XamlResourceUri.Create(profile.ResourceScheme, types.Compilation.AssemblyName ?? "Application", input.LogicalPath)
        : XamlResourceUri.Normalize(input.ResourceUri);
}
