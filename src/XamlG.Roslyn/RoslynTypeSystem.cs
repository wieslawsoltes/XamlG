using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Syntax;
namespace XamlG.Roslyn;

/// <summary>Thread-safe caches owned by one immutable Roslyn compilation. No process-wide symbol retention.</summary>
public sealed class RoslynTypeSystem
{
    private readonly ConcurrentDictionary<(string Namespace, string Name, int Arity), TypeResolution> _types = new();
    private readonly ConcurrentDictionary<string, INamedTypeSymbol?> _metadataTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ITypeSymbol, ImmutableArray<IMethodSymbol>> _addMethods = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, IMethodSymbol?> _markupMethods = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<IPropertySymbol>> _declaredContentProperties = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, IPropertySymbol?> _contentProperties = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamedTypeSymbol, string?> _contentErrors = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, bool> _usableDuringInitialization = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>> _namespaceTargets = new(StringComparer.Ordinal);
    private readonly ImmutableArray<IAssemblySymbol> _assemblies;
    private readonly ILookup<string, IAssemblySymbol> _assembliesByName;
    public RoslynTypeSystem(CSharpCompilation compilation, XamlTypeSystemConfiguration? configuration = null)
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation)); Configuration = configuration ?? new();
        _assemblies = ImmutableArray.Create(compilation.Assembly).AddRange(compilation.SourceModule.ReferencedAssemblySymbols);
        _assembliesByName = _assemblies.ToLookup(assembly => assembly.Identity.Name, StringComparer.Ordinal);
        var mappings = Configuration.NamespaceMappings.ToBuilder();
        foreach (var assembly in _assemblies)
            foreach (var attribute in assembly.GetAttributes())
            {
                if (attribute.AttributeClass == null || !Configuration.XmlnsDefinitionAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
                if (attribute.ConstructorArguments.Length < 2 || attribute.ConstructorArguments[0].Value is not string xml || attribute.ConstructorArguments[1].Value is not string clr) continue;
                var target = attribute.NamedArguments.FirstOrDefault(p => p.Key == "AssemblyName").Value.Value as string ?? assembly.Identity.Name;
                mappings.Add(new(xml, clr, target));
            }
        NamespaceMappings = mappings.Distinct().ToImmutableArray();
    }
    public CSharpCompilation Compilation { get; }
    public XamlTypeSystemConfiguration Configuration { get; }
    public ImmutableArray<XmlNamespaceMapping> NamespaceMappings { get; }
    public INamedTypeSymbol? Find(string metadataName) => _metadataTypes.TryGetValue(metadataName, out var type)
        ? type : _metadataTypes.GetOrAdd(metadataName, Compilation.GetTypeByMetadataName);
    public INamedTypeSymbol Special(SpecialType type) => Compilation.GetSpecialType(type);
    public bool IsAccessible(ISymbol symbol, INamedTypeSymbol? within = null) => Compilation.IsSymbolAccessibleWithin(symbol, (ISymbol?)within ?? Compilation.Assembly);
    /// <summary>Resolves provider alternatives independently of the assignment target, preferring
    /// parameterless providers and then typed results within each parameter shape.</summary>
    public IMethodSymbol? MarkupExtensionMethod(ITypeSymbol type) => _markupMethods.TryGetValue(type, out var method)
        ? method : _markupMethods.GetOrAdd(type, SelectMarkupExtensionMethod);
    private IMethodSymbol? SelectMarkupExtensionMethod(ITypeSymbol type) => type.Members().OfType<IMethodSymbol>()
        .Where(method => (method.Name == Configuration.MarkupExtensionMethod || method.Name == Configuration.TypedMarkupExtensionMethod) &&
            !method.IsStatic && !method.IsGenericMethod && !method.ReturnsVoid && !method.ReturnsByRef && !method.ReturnsByRefReadonly && IsAccessible(method) &&
            (method.Parameters.Length == 0 || method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None && method.Parameters[0].Type.HasMetadataName(ClrNames.IServiceProvider)))
        .OrderBy(method => method.Parameters.Length)
        .ThenBy(method => method.ReturnType.SpecialType == SpecialType.System_Object ? 1 : 0)
        .FirstOrDefault();
    public TypeResolution Resolve(string xmlNamespace, string name, int arity = 0) => _types.TryGetValue((xmlNamespace, name, arity), out var type)
        ? type : _types.GetOrAdd((xmlNamespace, name, arity), key => ResolveCore(key.Namespace, key.Name, key.Arity));
    private TypeResolution ResolveCore(string xmlNamespace, string name, int arity)
    {
        if (XamlNames.IsLanguage(xmlNamespace))
        {
            var special = XamlIntrinsicTypes.GetSpecialType(name);
            var intrinsic = special == SpecialType.None ? Find("System." + name) : Special(special);
            return intrinsic != null && intrinsic.Arity == arity && IsAccessible(intrinsic) ? new(intrinsic, ImmutableArray.Create(intrinsic)) : TypeResolution.Missing;
        }
        var targets = _namespaceTargets.TryGetValue(xmlNamespace, out var cached)
            ? cached : _namespaceTargets.GetOrAdd(xmlNamespace, ResolveNamespaceTargets);
        var candidates = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var metadata = name + (arity == 0 ? string.Empty : "`" + arity);
        foreach (var target in targets)
        {
            var qualified = target.Prefix + metadata;
            // Facades such as netstandard carry forwarders rather than definitions.
            // Preserve the resolved destination symbol's actual assembly identity.
            var type = target.Assembly.GetTypeByMetadataName(qualified) ?? target.Assembly.ResolveForwardedType(qualified);
            if (type != null && IsAccessible(type)) candidates.Add(type);
        }
        var result = candidates.OrderBy(c => c.ContainingAssembly.Identity.ToString(), StringComparer.Ordinal).ThenBy(c => c.MetadataName(), StringComparer.Ordinal).ToImmutableArray();
        return new(result.Length == 1 ? result[0] : null, result);
    }
    private ImmutableArray<(string Prefix, IAssemblySymbol Assembly)> ResolveNamespaceTargets(string xmlNamespace)
    {
        // Namespace and assembly selection is independent of the requested type.
        // Normalize assembly names once, instead of splitting one for every pair
        // of mapping and reference on each type's first lookup.
        var mappings = new List<XmlNamespaceMapping>();
        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var pieces = xmlNamespace.Substring(14).Split(';');
            var assembly = pieces.Skip(1).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?.Substring(9) ?? Configuration.DefaultAssemblyName ?? Compilation.AssemblyName;
            mappings.Add(new(xmlNamespace, pieces[0], assembly));
        }
        else if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal)) mappings.Add(new(xmlNamespace, xmlNamespace.Substring(6)));
        else mappings.AddRange(NamespaceMappings.Where(m => m.XmlNamespace == xmlNamespace));
        var targets = ImmutableArray.CreateBuilder<(string Prefix, IAssemblySymbol Assembly)>();
        foreach (var mapping in mappings)
        {
            var prefix = string.IsNullOrEmpty(mapping.ClrNamespace) ? string.Empty : mapping.ClrNamespace + ".";
            IEnumerable<IAssemblySymbol> assemblies = _assemblies;
            if (mapping.AssemblyName is { } assemblyName)
            {
                var comma = assemblyName.IndexOf(',');
                assemblies = _assembliesByName[(comma < 0 ? assemblyName : assemblyName.Substring(0, comma)).Trim()];
            }
            foreach (var assembly in assemblies) targets.Add((prefix, assembly));
        }
        return targets.ToImmutable();
    }
    public ImmutableArray<IPropertySymbol> GetDeclaredContentProperties(INamedTypeSymbol type) =>
        _declaredContentProperties.TryGetValue(type, out var properties) ? properties : _declaredContentProperties.GetOrAdd(type, FindDeclaredContentProperties);
    private ImmutableArray<IPropertySymbol> FindDeclaredContentProperties(INamedTypeSymbol type)
    {
        var result = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            if (!property.IsStatic && property.HasAttribute(Configuration.ContentAttributes)) result.Add(property);
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass == null || !Configuration.ContentAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
            var name = ContentPropertyName(attribute);
            if (name != null && type.Members(name).OfType<IPropertySymbol>().FirstOrDefault() is { } property) result.Add(property);
        }
        return result.ToImmutableArray();
    }
    private string? ContentPropertyName(AttributeData attribute) => attribute.ConstructorArguments.FirstOrDefault().Value as string ??
        attribute.NamedArguments.FirstOrDefault(p => p.Key == Configuration.ContentPropertyAttributeProperty).Value.Value as string;
    public string? GetContentPropertyError(INamedTypeSymbol type) => _contentErrors.TryGetValue(type, out var error)
        ? error : _contentErrors.GetOrAdd(type, FindContentPropertyError);
    private string? FindContentPropertyError(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var properties = GetDeclaredContentProperties(current);
            if (properties.Length > 1) return $"Type '{current}' declares more than one content property.";
            foreach (var attribute in current.GetAttributes())
            {
                if (attribute.AttributeClass == null || !Configuration.ContentAttributes.Contains(attribute.AttributeClass.MetadataName())) continue;
                var name = ContentPropertyName(attribute);
                if (name == null) return $"Content attribute on '{current}' must specify a property name.";
                if (!current.Members(name).OfType<IPropertySymbol>().Any()) return $"Content property '{current}.{name}' does not exist.";
            }
            if (properties.Length != 0) break;
        }
        return null;
    }
    public IPropertySymbol? GetContentProperty(INamedTypeSymbol type) => _contentProperties.TryGetValue(type, out var property)
        ? property : _contentProperties.GetOrAdd(type, FindContentProperty);
    private IPropertySymbol? FindContentProperty(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        { var properties = GetDeclaredContentProperties(current); if (properties.Length != 0) return properties[0]; }
        return null;
    }
    public bool HasInheritedAttribute(ITypeSymbol type, IEnumerable<string> names)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType) if (current.HasAttribute(names)) return true;
        return false;
    }
    public bool IsUsableDuringInitialization(ITypeSymbol type) => _usableDuringInitialization.TryGetValue(type, out var usable)
        ? usable : _usableDuringInitialization.GetOrAdd(type, ReadUsableDuringInitialization);
    private bool ReadUsableDuringInitialization(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current != null; current = current.BaseType)
            foreach (var attribute in current.GetAttributes())
                if (attribute.AttributeClass != null && Configuration.UsableDuringInitializationAttributes.Contains(attribute.AttributeClass.MetadataName()))
                    return attribute.ConstructorArguments.Length == 0 || attribute.ConstructorArguments[0].Value is true;
        return false;
    }
    public ImmutableArray<IMethodSymbol> AddMethods(ITypeSymbol type) => _addMethods.TryGetValue(type, out var methods)
        ? methods : _addMethods.GetOrAdd(type, CollectAddMethods);
    private ImmutableArray<IMethodSymbol> CollectAddMethods(ITypeSymbol type)
    {
        var methods = new List<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        void Add(IMethodSymbol method)
        {
            if (method.Parameters.All(parameter => parameter.RefKind == RefKind.None) && seen.Add(method)) methods.Add(method);
        }
        foreach (var method in type.Members(Configuration.CollectionAddMethod).OfType<IMethodSymbol>())
            if (!method.IsStatic && !method.IsGenericMethod && method.Parameters.Length is 1 or 2 && IsAccessible(method)) Add(method);
        foreach (var contract in type.AllInterfaces)
        {
            foreach (var method in contract.GetMembers(Configuration.CollectionAddMethod).OfType<IMethodSymbol>())
                if (method.Parameters.Length is 1 or 2 && !method.IsStatic && !method.IsGenericMethod && IsAccessible(contract) && IsAccessible(method)) Add(method);
        }
        if (methods.Count == 0 && type is INamedTypeSymbol declared)
        {
            foreach (var projection in Configuration.CollectionProjections)
            {
                if (!declared.OriginalDefinition.HasMetadataName(projection.DeclaredMetadataName)) continue;
                var mutation = Find(projection.MutationMetadataName);
                if (mutation == null || mutation.Arity != declared.Arity) continue;
                if (mutation.Arity != 0) mutation = mutation.Construct(declared.TypeArguments.ToArray());
                foreach (var method in mutation.Members(Configuration.CollectionAddMethod).OfType<IMethodSymbol>())
                    if (!method.IsStatic && !method.IsGenericMethod && method.Parameters.Length == 1)
                        Add(method);
            }
        }
        methods = methods.OrderBy(method => SymbolEqualityComparer.Default.Equals(method.ContainingType, type) ? 0 : 1)
            .ThenBy(method => method.ContainingType.TypeKind == TypeKind.Interface ? 1 : 0).ToList();
        // Ordinary Add methods precede child protocols. Include the declared interface
        // itself, then prefer typed child contracts over their object-valued fallback.
        if (!Configuration.AddChildInterfaces.IsDefaultOrEmpty)
        {
            var childContracts = type is INamedTypeSymbol named ? type.AllInterfaces.Insert(0, named) : type.AllInterfaces;
            foreach (var contract in childContracts.Where(contract => Configuration.AddChildInterfaces.Contains(contract.OriginalDefinition.MetadataName()))
                         .OrderByDescending(contract => contract.Arity != 0))
                foreach (var method in contract.GetMembers(Configuration.AddChildMethod).OfType<IMethodSymbol>())
                    if (method.Parameters.Length is 1 or 2 && !method.IsStatic && !method.IsGenericMethod && IsAccessible(contract) && IsAccessible(method)) Add(method);
        }
        return methods.ToImmutableArray();
    }
    public IEnumerable<INamedTypeSymbol> EnumerateTypes(string xmlNamespace)
    {
        var namespaces = NamespaceMappings.Where(m => m.XmlNamespace == xmlNamespace).ToArray();
        foreach (var assembly in _assemblies)
            foreach (var mapping in namespaces.Where(m => m.AssemblyName == null || m.AssemblyName == assembly.Identity.Name))
            {
                INamespaceSymbol? scope = assembly.GlobalNamespace;
                foreach (var part in mapping.ClrNamespace.Split('.')) if (part.Length != 0) scope = scope?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (scope != null) foreach (var type in scope.GetTypeMembers()) if (IsAccessible(type)) yield return type;
            }
    }
}
