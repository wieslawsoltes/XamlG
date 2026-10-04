using Microsoft.CodeAnalysis;

namespace XamlG.Compiler.Resources;

/// <summary>A compile-time resource identity. Exactly one of LocalDocumentId and ExternalFactory is set.</summary>
public sealed record XamlResourceDescriptor(string Uri, INamedTypeSymbol RootType,
    string? LocalDocumentId, string GeneratedNamespace, IMethodSymbol? ExternalFactory);
