using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

/// <summary>Planned changes to a source snapshot. Null version denotes source outside the XAML editor store.</summary>
public sealed record XamlDocumentEdits(string Path, string OriginalText, long? Version, ImmutableArray<XamlTextChange> Changes);
