using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

/// <summary>Emission-local namespace planning. Caches generated text, never runtime
/// dictionaries or namespace item instances; no static cache retains compilations.</summary>
internal sealed class NamespaceEmissionPlan
{
    private readonly EmissionContext _context;
    private readonly Dictionary<NamespaceScope, ScopeLayout> _scopes = new();
    private Dictionary<string, List<XmlNamespaceMapping>>? _mappings;
    private ImmutableArray<ContractPlan> _contracts;

    public NamespaceEmissionPlan(EmissionContext context) => _context = context;

    public ScopeLayout GetScope(NamespaceScope scope)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        if (_scopes.TryGetValue(scope, out var layout)) return layout;
        layout = ScopeLayout.Create(scope);
        _context.Cancellation.ThrowIfCancellationRequested();
        _scopes.Add(scope, layout);
        return layout;
    }

    public ImmutableArray<ContractPlan> Contracts
    {
        get
        {
            _context.Cancellation.ThrowIfCancellationRequested();
            if (!_contracts.IsDefault) return _contracts;
            var result = ImmutableArray.CreateBuilder<ContractPlan>();
            foreach (var contract in _context.Document.Runtime.Services)
            {
                if (contract.Mapping.Kind != XamlServiceKind.XmlNamespaces ||
                    contract.NamespaceItemType == null || contract.NamespaceNameProperty == null ||
                    contract.AssemblyNameProperty == null) continue;
                result.Add(new(this, contract));
            }
            _context.Cancellation.ThrowIfCancellationRequested();
            return _contracts = result.ToImmutable();
        }
    }

    private List<XmlNamespaceMapping>? GetMappings(string uri)
    {
        if (_mappings == null)
        {
            var index = new Dictionary<string, List<XmlNamespaceMapping>>(StringComparer.Ordinal);
            foreach (var mapping in _context.Document.Runtime.NamespaceMappings)
            {
                _context.Cancellation.ThrowIfCancellationRequested();
                // A malformed custom mapping with no URI matched no ordinary alias
                // in the former equality filter either. Do not make it a null key.
                if (mapping.XmlNamespace == null) continue;
                if (!index.TryGetValue(mapping.XmlNamespace, out var values))
                    index.Add(mapping.XmlNamespace, values = new());
                values.Add(mapping);
            }
            _context.Cancellation.ThrowIfCancellationRequested();
            _mappings = index;
        }
        return _mappings.TryGetValue(uri, out var matches) ? matches : null;
    }

    internal sealed class ScopeLayout
    {
        private ScopeLayout(ImmutableArray<KeyValuePair<string, string>> bindings, string key)
        { Bindings = bindings; Key = key; }
        public ImmutableArray<KeyValuePair<string, string>> Bindings { get; }
        public string Key { get; }

        public static ScopeLayout Create(NamespaceScope scope)
        {
            var bindings = scope.Bindings.Where(pair => scope.DeclaredPrefixes.Contains(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToImmutableArray();
            // Preserve the existing helper identity byte-for-byte, including the
            // explicit-prefix filter and ordinal ordering. Runtime alias entries
            // are emitted from this same ordered snapshot rather than sorted again.
            return new(bindings, string.Join("\n", bindings.Select(pair => pair.Key + "=" + pair.Value)));
        }
    }

    internal sealed class ContractPlan
    {
        private readonly NamespaceEmissionPlan _owner;
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly string _namespaceName;
        private readonly string _assemblyName;
        private readonly string _emptyArray;

        public ContractPlan(NamespaceEmissionPlan owner, BoundServiceContract contract)
        {
            _owner = owner;
            Contract = contract;
            ItemType = contract.NamespaceItemType!.CSharpName();
            _namespaceName = CSharpNames.Identifier(contract.NamespaceNameProperty!.Name);
            _assemblyName = CSharpNames.Identifier(contract.AssemblyNameProperty!.Name);
            _emptyArray = "new " + ItemType + "[] {  }";
        }

        public BoundServiceContract Contract { get; }
        public string ItemType { get; }

        public string GetArrayExpression(string uri)
        {
            _owner._context.Cancellation.ThrowIfCancellationRequested();
            if (_values.TryGetValue(uri, out var expression)) return expression;
            var mappings = _owner.GetMappings(uri);
            if (mappings == null) return _emptyArray;
            var items = new string[mappings.Count];
            for (var i = 0; i < items.Length; i++)
            {
                _owner._context.Cancellation.ThrowIfCancellationRequested();
                var mapping = mappings[i];
                items[i] = "new " + ItemType + " { " + _namespaceName + " = " + CSharpNames.Literal(mapping.ClrNamespace) + ", " +
                    _assemblyName + " = " + (mapping.AssemblyName == null ? "null" : CSharpNames.Literal(mapping.AssemblyName)) + " }";
            }
            expression = "new " + ItemType + "[] { " + string.Join(", ", items) + " }";
            _owner._context.Cancellation.ThrowIfCancellationRequested();
            _values.Add(uri, expression);
            return expression;
        }
    }
}
