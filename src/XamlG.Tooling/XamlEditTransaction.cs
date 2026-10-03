using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling;

public sealed record XamlEditTransaction(long ExpectedRevision, string Description, ImmutableArray<XamlTextChange> Changes);
