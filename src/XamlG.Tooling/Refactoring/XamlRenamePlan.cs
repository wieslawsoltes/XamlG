using System.Collections.Immutable;
using XamlG.Syntax;

namespace XamlG.Tooling.Refactoring;

public sealed record XamlRenamePlan(string OldName, string NewName, TextSpan TriggerSpan, ImmutableArray<XamlDocumentEdits> Documents);
