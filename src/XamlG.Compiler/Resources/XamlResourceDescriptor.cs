using Microsoft.CodeAnalysis;

namespace XamlG.Compiler.Resources;

/// <summary>A compile-time resource identity. Exactly one of LocalDocumentId and ExternalFactory is set.</summary>
public sealed record XamlResourceDescriptor(string Uri, INamedTypeSymbol RootType,
    string? LocalDocumentId, string GeneratedNamespace, IMethodSymbol? ExternalFactory)
{
    /// <summary>Optional local code-behind factory; classless resources use the generated document class.</summary>
    public INamedTypeSymbol? LocalFactoryType { get; init; }
    public string? LocalFactoryMethod { get; init; }
}
