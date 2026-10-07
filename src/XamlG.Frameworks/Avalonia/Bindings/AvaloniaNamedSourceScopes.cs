using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;

namespace XamlG.Frameworks.Avalonia.Bindings;

/// <summary>Shares inferred source scopes and their compiled values with later object binding.</summary>
internal sealed class AvaloniaNamedSourceScopes(BindingContext context, ObjectBindingBuilder root)
{
    private static readonly XamlAnnotationKey<AvaloniaNamedSourceScopes> Key = new("Avalonia.NamedSourceScopes");
    private readonly Dictionary<XamlElementSyntax, ObjectBindingBuilder> _objects = new();
    private AvaloniaNamedSourceCatalog? _catalog;
    private XamlElementSyntax? _metadataTarget;
    public bool CompileBindingsByDefault { get; set; }

    public IDisposable EnterMetadata(XamlElementSyntax syntax) => new MetadataScope(this, syntax);

    public static AvaloniaNamedSourceScopes Get(BindingContext context)
    {
        var root = context.Ancestors.Last();
        if (!root.Annotations.TryGet(Key, out var scopes)) root.Annotations.Set(Key, scopes = new(context, root));
        return scopes;
    }

    public bool Restore(ObjectBindingBuilder target)
    {
        var restored = _objects.TryGetValue(target.Syntax, out var previous) && !ReferenceEquals(previous, target) &&
            previous.Annotations.TryGet(AvaloniaBindingScope.Key, out _);
        if (restored)
        {
            previous!.Annotations.TryGet(AvaloniaBindingScope.Key, out var scope);
            target.Annotations.Set(AvaloniaBindingScope.Key, scope);
            if (previous.Annotations.TryGet(AvaloniaDataTypeMetadata.MappedDirective, out var property))
                target.Annotations.Set(AvaloniaDataTypeMetadata.MappedDirective, property);
            AvaloniaCompiledBindingRule.ShareResults(previous, target);
        }
        _objects[target.Syntax] = target;
        return restored;
    }

    public (INamedTypeSymbol? Type, ITypeSymbol? DataType) Find(string name)
    {
        _catalog ??= new(context, root);
        var found = _catalog.Find(name);
        if (found == null) return (null, null);
        var limit = _metadataTarget == null ? int.MaxValue : _catalog.Order(_metadataTarget);
        var chain = new Stack<AvaloniaNamedSourceCatalog.Entry>();
        for (var current = found; current != null; current = current.Parent) chain.Push(current);
        var original = context.Ancestors.ToArray();
        var nameScopes = chain.ToDictionary(entry => entry.Syntax, entry => _catalog.NameScope(entry));
        var properties = new Stack<XamlPropertyBindingScope>();
        using var suspended = context.SuspendPropertyScope();
        context.Ancestors.Clear();
        try
        {
            foreach (var entry in chain)
            {
                if (entry.Member != null && context.Ancestors.FirstOrDefault() is { } parent)
                    properties.Push(context.EnterPropertyScope(parent, entry.Member));
                var exists = _objects.TryGetValue(entry.Syntax, out var builder);
                builder ??= entry.Builder(nameScopes[entry.Syntax]);
                context.Ancestors.Push(builder);
                if (!exists && entry.Order < limit) new AvaloniaBindingScopeRule(CompileBindingsByDefault).Initialize(context, builder);
            }
            // Name lookup starts a separate metadata walk at the chosen namescope root.
            for (var entry = found; entry != null && entry.ScopeRoot == found.ScopeRoot; entry = entry.Parent)
                if (entry.Order < limit && _objects.TryGetValue(entry.Syntax, out var owner) &&
                    owner.Annotations.TryGet(AvaloniaBindingScope.Key, out var scope) && scope.HasOwnDataTypeMetadata)
                    return (found.Type, scope.DataType);
            return (found.Type, null);
        }
        finally
        {
            while (properties.Count != 0) properties.Pop().Dispose();
            context.Ancestors.Clear();
            for (var index = original.Length - 1; index >= 0; index--) context.Ancestors.Push(original[index]);
        }
    }

    private sealed class MetadataScope : IDisposable
    {
        private readonly AvaloniaNamedSourceScopes _owner;
        private readonly XamlElementSyntax? _previous;
        public MetadataScope(AvaloniaNamedSourceScopes owner, XamlElementSyntax syntax)
        {
            _owner = owner; _previous = owner._metadataTarget;
            owner._metadataTarget = syntax;
        }
        public void Dispose() => _owner._metadataTarget = _previous;
    }
}
