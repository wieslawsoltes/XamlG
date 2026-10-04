using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

public sealed record XamlCodeAction(string Title, string Kind, bool IsPreferred, ImmutableArray<XamlTextChange> Changes);
