using System.Collections.Immutable;
using XamlG.Roslyn;

namespace XamlG.Compiler;

/// <summary>Composable immutable language policy. Compiler hosts consume the same profile and passes.</summary>
public sealed record XamlFrameworkProfile
{
    public static XamlFrameworkProfile Portable { get; } = new();
    public string Name { get; init; } = "Portable";
    public XamlLoaderConfiguration? SourceLoader { get; init; }
    public ImmutableArray<XamlG.Compiler.References.IXamlNameReferenceRule> NameReferenceRules { get; init; } = ImmutableArray.Create<XamlG.Compiler.References.IXamlNameReferenceRule>(new XamlG.Compiler.References.IntrinsicNameReferenceRule());
    public string ResourceScheme { get; init; } = "xamlg";
    public ImmutableDictionary<string, string> ResourceSourceMembers { get; init; } = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    public XamlTypeSystemConfiguration TypeSystem { get; init; } = new();
    public XamlRuntimeConfiguration Runtime { get; init; } = new();
    public ImmutableArray<IXamlObjectExpressionRule> ObjectExpressionRules { get; init; } = ImmutableArray<IXamlObjectExpressionRule>.Empty;
    public ImmutableArray<IXamlTypeBindingRule> TypeBindingRules { get; init; } = ImmutableArray<IXamlTypeBindingRule>.Empty;
    public ImmutableArray<IXamlBindingRule> BindingRules { get; init; } = ImmutableArray<IXamlBindingRule>.Empty;
    public ImmutableArray<IXamlObjectBindingRule> ObjectBindingRules { get; init; } = ImmutableArray<IXamlObjectBindingRule>.Empty;
    public ImmutableArray<IXamlMemberBindingRule> MemberBindingRules { get; init; } = ImmutableArray<IXamlMemberBindingRule>.Empty;
    public ImmutableArray<IXamlPropertyBindingRule> PropertyBindingRules { get; init; } = ImmutableArray<IXamlPropertyBindingRule>.Empty;
    public ImmutableArray<IXamlMarkupBindingRule> MarkupBindingRules { get; init; } = ImmutableArray<IXamlMarkupBindingRule>.Empty;
    public ImmutableArray<IXamlTextConversionRule> TextConversionRules { get; init; } = ImmutableArray<IXamlTextConversionRule>.Empty;
    public ImmutableArray<IXamlDocumentPass> Passes { get; init; } = ImmutableArray<IXamlDocumentPass>.Empty;
}
