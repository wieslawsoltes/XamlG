using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlParserTests;

/// <summary>Maps the upstream IL transform's policy into the compiler's public symbol-based setter provider.</summary>
public sealed class UpstreamDynamicSetterProvider : IXamlValueSetterProvider
{
    public ImmutableArray<BoundValueSetter> GetSetters(BindingContext context, ObjectBindingBuilder target, BoundMember member)
    {
        if (member.Name != nameof(ISpecialHandling<object, object>.Value) ||
            !target.Type.AllInterfaces.Any(t => t.OriginalDefinition.HasMetadataName("XamlParserTests.ISpecialHandling`2")))
            return ImmutableArray<BoundValueSetter>.Empty;
        var property = target.Type.Members(nameof(ISpecialHandling<object, object>.SpecialHandler)).OfType<IPropertySymbol>().Single();
        var method = property.Type.Members(nameof(SpecialHandler<object>.Handle)).OfType<IMethodSymbol>().Single();
        return ImmutableArray.Create<BoundValueSetter>(new BoundPropertyValueSetter(member),
            new BoundMethodValueSetter(method, ImmutableArray.Create(property)));
    }
}
