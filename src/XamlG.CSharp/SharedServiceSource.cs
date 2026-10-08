using System.Collections.Immutable;

namespace XamlG.CSharp;

internal sealed record SharedServiceSource(string TypeName, string Source, ImmutableDictionary<string, string> NamespaceFactories);
