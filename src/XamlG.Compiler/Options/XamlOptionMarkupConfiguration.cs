using System.Collections.Immutable;
namespace XamlG.Compiler.Options;

/// <summary>Framework metadata defining a compiler-lowered options extension.</summary>
public sealed record XamlOptionMarkupConfiguration(string PredicateMethod, string OptionAttribute,
    string DefaultAttribute, ImmutableArray<string> EntryTypes, string OptionsProperty, string ContentProperty)
{
    public bool AllowRepeatedAssignments { get; init; }
    public bool UseFirstMatchingPredicate { get; init; }
}
