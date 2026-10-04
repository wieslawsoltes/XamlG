using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp.Resources;

/// <summary>One eligibility/signature policy shared by indexing and emission.</summary>
internal static class XamlClassFactory
{
    public static bool CanCreate(INamedTypeSymbol type, XamlCompilerOptions options, XamlElementSyntax? root = null)
    {
        if (root != null)
        {
            var scope = NamespaceScope.Empty.Push(root);
            if (scope.Directive(root, "FactoryMethod") != null || root.Children.OfType<XamlElementSyntax>().Any(child =>
            {
                var name = scope.Push(child).Expand(child.Name);
                return name.Namespace != null && XamlNames.IsLanguage(name.Namespace) && name.LocalName == "Arguments";
            })) return false; // Explicit construction requires the caller-controlled Populate API.
        }
        if (!options.GenerateBuildMethod || !options.GenerateInitializeComponent || type.TypeKind != TypeKind.Class || type.IsAbstract ||
            type.GetMembers("InitializeComponent").Any()) return false;
        for (var current = type; current != null; current = current.ContainingType)
            if (current.Arity != 0) return false;
        var constructor = Constructor(type);
        if (constructor == null) return false;
        var required = type.Members().Any(m => m is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });
        return !required || constructor.GetAttributes().Any(a => a.AttributeClass?.HasMetadataName("System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") == true);
    }
    public static IMethodSymbol? Constructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0) ??
        type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 1 && c.Parameters[0].RefKind == RefKind.None && c.Parameters[0].Type.HasMetadataName("System.IServiceProvider"));
    public static string Method(string documentId) => "__XamlGBuild_" + CSharpNames.StableId(documentId);
}
