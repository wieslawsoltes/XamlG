using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Frameworks.Avalonia.Styling;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

internal static class AvaloniaItemTypeInference
{
    public static ITypeSymbol? Read(BindingContext context)
    {
        var property = context.PropertyScope;
        if (property == null || Dependency(property.Member) is not { } dependency) return null;
        var (sourceName, ancestorType) = dependency;
        var owner = ancestorType == null ? property.Target : context.Ancestors
            .SkipWhile(ancestor => !ReferenceEquals(ancestor, property.Target))
            .FirstOrDefault(ancestor => context.Types.Compilation.ClassifyCommonConversion(ancestor.Type, ancestorType).IsImplicit);
        if (owner == null) return null;
        var source = BindingPropertyValue.Read(context, owner).FirstOrDefault(value => value.MemberName == sourceName);
        var node = source?.Values.FirstOrDefault(value => value is XamlElementSyntax || value is XamlTextSyntax text && !string.IsNullOrWhiteSpace(text.Value));
        if (source == null || node == null) return null;
        var member = context.Members.Resolve(owner.Type, source.Name, source.Scope, source.NameSpan, report: false);
        if (member == null) return null;

        ITypeSymbol? collectionType;
        using (new OwnerScope(context, owner, member))
        {
            var readInput = CompiledBindingValueReader.Read(context, owner, node, source.Scope, out var type);
            if (readInput != null)
            {
                if (!owner.Annotations.TryGet(AvaloniaBindingScope.Key, out var configuration) || !configuration.HasDataTypeMetadata) return null;
                collectionType = AvaloniaCompiledBindingRule.Bind(context, owner, member.ValueType, node.Span, readInput, inferDataContext: true)?.ValueType;
            }
            else
            {
                if (type != null && (AvaloniaStyleScope.Is(type, AvaloniaMetadata.BindingBase) ||
                    type.HasMetadataName(AvaloniaBindingMetadata.ReflectionExtension) || type.HasMetadataName(AvaloniaBindingMetadata.BindingExtension))) return null;
                collectionType = context.Values.PeekValueType(node, source.Scope, owner.NameScopeId);
            }
        }
        if (collectionType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } enumerable)
            return enumerable.TypeArguments[0];
        return collectionType?.AllInterfaces.FirstOrDefault(type => type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];
    }

    internal static (string Source, ITypeSymbol? Ancestor)? Dependency(BoundMember member)
    {
        var attribute = member.Symbol.GetAttributes().FirstOrDefault(value =>
            value.AttributeClass?.HasMetadataName(AvaloniaBindingMetadata.InheritDataTypeFromItems) == true);
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is not string { Length: > 0 } source) return null;
        return (source, attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "AncestorType").Value.Value as ITypeSymbol);
    }

    private sealed class OwnerScope : IDisposable
    {
        private readonly BindingContext _context;
        private readonly List<ObjectBindingBuilder> _descendants = new();
        private readonly XamlPropertyBindingScope _property;
        private readonly AvaloniaBindingTargetScope _expected;
        public OwnerScope(BindingContext context, ObjectBindingBuilder owner, BoundMember member)
        {
            _context = context;
            while (context.Ancestors.Count != 0 && !ReferenceEquals(context.Ancestors.Peek(), owner))
                _descendants.Add(context.Ancestors.Pop());
            _property = context.EnterPropertyScope(owner, member);
            _expected = new(owner, member.ValueType);
        }
        public void Dispose()
        {
            _expected.Dispose();
            _property.Dispose();
            for (var index = _descendants.Count - 1; index >= 0; index--) _context.Ancestors.Push(_descendants[index]);
        }
    }
}
