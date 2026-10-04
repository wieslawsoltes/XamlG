using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>Indexes local root signatures and referenced export metadata without compiling or executing a factory.</summary>
public static class XamlResourceCatalogBuilder
{
    public static XamlResourceCatalog Create(IReadOnlyList<XamlProjectDocument> documents, RoslynTypeSystem types,
        XamlFrameworkProfile profile, XamlCompilerOptions options, CancellationToken cancellationToken = default)
    {
        var resources = new List<XamlResourceDescriptor>();
        foreach (var input in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = input.Syntax.Root;
            if (root == null || !options.GenerateBuildMethod) continue;
            var scope = NamespaceScope.Empty.Push(root);
            if (scope.Directive(root, "Class") != null) continue;
            string uri;
            try { uri = Address(input, types, profile); }
            catch (ArgumentException) { continue; }
            var context = new BindingContext(input.Syntax, types, profile, options, cancellationToken);
            var type = context.ResolveType(root.Name, scope, root.NameSpan, scope.Directive(root, "TypeArguments")?.Value, report: false);
            if (type != null) resources.Add(new(uri, type, input.LogicalPath, options.GeneratedNamespace, null));
        }
        foreach (var assembly in types.Compilation.SourceModule.ReferencedAssemblySymbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass?.HasMetadataName(XamlResourceMetadata.ExportAttribute) != true || attribute.ConstructorArguments.Length != 3) continue;
                if (attribute.ConstructorArguments[0].Value is not string uri ||
                    attribute.ConstructorArguments[1].Value is not INamedTypeSymbol factory ||
                    attribute.ConstructorArguments[2].Value is not string name) continue;
                var methods = factory.GetMembers(name).OfType<IMethodSymbol>().Where(m => m.IsStatic && !m.IsGenericMethod &&
                    m.Parameters.Length == 1 && m.Parameters[0].Type.HasMetadataName(XamlResourceMetadata.ServiceProvider) &&
                    m.ReturnType is INamedTypeSymbol && !m.ReturnsVoid && types.IsAccessible(m)).ToArray();
                if (methods.Length != 1 || !types.IsAccessible(factory)) continue;
                try { uri = XamlResourceUri.Normalize(uri); }
                catch (ArgumentException) { continue; }
                resources.Add(new(uri, (INamedTypeSymbol)methods[0].ReturnType, null, string.Empty, methods[0]));
            }
        }
        return new(resources);
    }
    public static string Address(XamlProjectDocument input, RoslynTypeSystem types, XamlFrameworkProfile profile) => input.ResourceUri == null
        ? XamlResourceUri.Create(profile.ResourceScheme, types.Compilation.AssemblyName ?? "Application", input.LogicalPath)
        : XamlResourceUri.Normalize(input.ResourceUri);
}
