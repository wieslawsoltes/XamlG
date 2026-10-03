namespace XamlG.Roslyn;

/// <summary>
/// A collection API exposed through a more general declared property type. The generated
/// code casts to the configured mutation contract; it never searches for methods at runtime.
/// </summary>
public sealed record XamlCollectionProjection(string DeclaredMetadataName, string MutationMetadataName);
