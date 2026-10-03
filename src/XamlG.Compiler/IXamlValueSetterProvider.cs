using System.Collections.Immutable;

namespace XamlG.Compiler;

/// <summary>Supplies ordered, symbol-bound alternatives for a framework's polymorphic property assignment.</summary>
public interface IXamlValueSetterProvider
{
    ImmutableArray<BoundValueSetter> GetSetters(BindingContext context, ObjectBindingBuilder target, BoundMember member);
}
