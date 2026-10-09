using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.CSharp;
internal sealed class EmissionContext : IDisposable
{
    private readonly System.Security.Cryptography.SHA256 _hash = System.Security.Cryptography.SHA256.Create();
    private byte[] _hashInput = Array.Empty<byte>();
    private int _temporary;
    private bool _sourceInfoSetter;
    private readonly Dictionary<ISymbol, string> _descriptors = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, string> _initSetters = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, int> _sourceRecords = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Index, PropertyAccessor Accessor)> _propertyAccessors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameNamespaces = new(StringComparer.Ordinal);
    public EmissionContext(BoundDocument document, CancellationToken cancellation)
    { Document = document; Cancellation = cancellation; Diagnostics.AddRange(document.Diagnostics); Id = StableId(document.Options.DocumentId ?? document.Syntax.Path); }
    public BoundDocument Document { get; }
    public string Id { get; }
    private ImmutableArray<NamedObjectField> _namedFields;
    public ImmutableArray<NamedObjectField> NamedFields => _namedFields.IsDefault
        ? _namedFields = Document.Root == null ? ImmutableArray<NamedObjectField>.Empty : NamedObjectField.Collect(Document.Root) : _namedFields;
    public CancellationToken Cancellation { get; }
    private DynamicSetterEmitter? _dynamicSetters;
    public DynamicSetterEmitter DynamicSetters => _dynamicSetters ??= new(this);
    private DynamicAddEmitter? _dynamicAdds;
    public DynamicAddEmitter DynamicAdds => _dynamicAdds ??= new(this);
    private CachedExpressionEmitter? _cachedExpressions;
    public CachedExpressionEmitter CachedExpressions => _cachedExpressions ??= new(this);
    public bool ShareCachedValues { get; set; }
    private ConstructionFactoryEmitter? _constructionFactories;
    public ConstructionFactoryEmitter ConstructionFactories => _constructionFactories ??= new(this);
    public ConstructionParameters? ConstructionParameters { get; set; }
    public CSharpWriter Writer { get; } = new();
    private TemporaryLocalPool? _locals;
    public TemporaryLocalPool Locals => _locals ??= new(this);
    public List<XamlDiagnostic> Diagnostics { get; } = new();
    public List<XamlSourceMapping> Mappings { get; } = new();
    public string RootVariable { get; set; } = "__root";
    public SharedServiceSource? SharedServices { get; set; }
    public SharedPropertyTables? SharedProperties { get; set; }
    public bool UsePropertyAliases { get; set; }
    public string ServicesType => SharedServices?.TypeName ?? "__XamlGServices_" + Id;
    public string Temporary(string role) => "__" + role + _temporary++;
    public string StableId(string value) => StableId(null, value, new(0, value.Length));
    public string StableId(string value, TextSpan span) => StableId(null, value, span);
    public string StableId(string? prefix, string value, TextSpan span)
    {
        // Source fingerprints hash slices of the existing document. Reuse one
        // emission-local UTF-8 buffer instead of copying each subtree into a
        // substring, concatenating its type name and allocating another byte array.
        var encoding = System.Text.Encoding.UTF8;
        var capacity = (prefix == null ? 0 : encoding.GetByteCount(prefix) + 1) + encoding.GetMaxByteCount(span.Length);
        if (_hashInput.Length < capacity) _hashInput = new byte[capacity];
        var offset = 0;
        if (prefix != null)
        {
            offset = encoding.GetBytes(prefix, 0, prefix.Length, _hashInput, 0);
            _hashInput[offset++] = 0;
        }
        var written = encoding.GetBytes(value, span.Start, span.Length, _hashInput, offset);
        return CSharpNames.StableId(_hash, _hashInput, offset + written);
    }
    public void Dispose() => _hash.Dispose();
    public string? FrameNamespaces(string frame) => _frameNamespaces.TryGetValue(frame, out var map) ? map : null;
    public void SetFrameNamespaces(string frame, string map) => _frameNamespaces[frame] = map;
    public void InheritFrameNamespaces(string frame, string parent)
    { if (FrameNamespaces(parent) is { } map) SetFrameNamespaces(frame, map); else _frameNamespaces.Remove(frame); }
    public void Map(TextSpan span, Action emit)
    {
        Cancellation.ThrowIfCancellationRequested();
        // Shared construction bodies have multiple source occurrences. Their
        // call arguments carry the occurrence-specific mappings instead.
        if (ConstructionParameters != null) { emit(); return; }
        if (Document.Options.EmitLineDirectives && Document.Syntax.Path.Length != 0)
        { var offset = Math.Min(span.Start, Document.Syntax.Text.Length); Writer.Line("#line " + (Document.Syntax.Lines.GetPosition(offset).Line + 1) + " " + CSharpNames.Literal(Document.Syntax.Path)); }
        var start = Writer.Position; emit(); Mappings.Add(new(new(start, Writer.Position - start), span, Document.Syntax.Path));
        if (Document.Options.EmitLineDirectives && Document.Syntax.Path.Length != 0) Writer.Line("#line default");
    }
    public string Descriptor(BoundMember member)
    {
        if (Document.Profile.Runtime.TargetPropertyMode == XamlTargetPropertyMode.Name)
            return CSharpNames.Literal(member.Name);
        if (_descriptors.TryGetValue(member.Symbol, out var name)) return name;
        name = "__descriptor_" + Id + "_" + _descriptors.Count; _descriptors.Add(member.Symbol, name); return name;
    }
    public string InitSetter(IMethodSymbol method)
    {
        if (_initSetters.TryGetValue(method, out var name)) return name;
        name = "__init_" + Id + "_" + _initSetters.Count; _initSetters.Add(method, name); return name;
    }
    public string SourceInfoTable => "__source_" + Id;
    public int SourceInfoIndex(string record)
    {
        if (!_sourceRecords.TryGetValue(record, out var index))
        { index = _sourceRecords.Count; _sourceRecords.Add(record, index); }
        return index;
    }
    public string SourceInfoSetter()
    {
        _sourceInfoSetter = true;
        return "__SetSourceInfo_" + Id;
    }
    public string PropertyRegistration(PropertyAccessor value, string frame)
    {
        var key = value.Key;
        if (SharedProperties?.Registrations.TryGetValue(key, out var shared) == true)
            return (UsePropertyAliases ? shared.Alias : shared.Source.TypeName) + ".Table.Register(" + frame + ", " + shared.Index + ");";
        if (!_propertyAccessors.TryGetValue(key, out var accessor))
        {
            accessor = (_propertyAccessors.Count, value);
            _propertyAccessors.Add(key, accessor);
        }
        return "__properties_" + Id + ".Register(" + frame + ", " + accessor.Index + ");";
    }
    public void EmitMetadataHelpers()
    {
        if (_sourceInfoSetter)
        {
            var source = Document.Runtime.SourceInfo!;
            Writer.Open("private static void " + SourceInfoSetter() + "(object __target, int __line, int __column)");
            Writer.Line(source.ObjectSetter.ContainingType.CSharpName() + "." + CSharpNames.Method(source.ObjectSetter) + "(__target, new " +
                source.Constructor.ContainingType.CSharpName() + "(__line, __column, " +
                (Document.Syntax.Path.Length == 0 ? "null" : CSharpNames.Literal(Document.Syntax.Path)) + "));");
            Writer.Close();
        }
        if (_propertyAccessors.Count != 0)
        {
            var accessors = _propertyAccessors.Values.OrderBy(accessor => accessor.Index).Select(accessor => accessor.Accessor).ToArray();
            PropertyTableEmitter.Emit(Writer, accessors, "__properties_" + Id, "__GetProperty_" + Id, "__SetProperty_" + Id, "private");
        }
        if (_sourceRecords.Count != 0)
        {
            var records = new System.Text.StringBuilder();
            foreach (var pair in _sourceRecords.OrderBy(pair => pair.Value))
                records.Append(pair.Key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(pair.Key);
            Writer.Line("private static readonly global::XamlG.Runtime.XamlSourceInfoTable __source_" + Id +
                " = global::XamlG.Runtime.XamlSourceInfoTable.FromEncoded(" + CSharpNames.Literal(Document.Syntax.Path) + ", " +
                Document.Syntax.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L, " + CSharpNames.Literal(records.ToString()) + ");");
        }
        foreach (var pair in _descriptors)
        {
            var type = pair.Key.ContainingType.CSharpName(); var name = CSharpNames.Literal(pair.Key.Name); string expression;
            if (pair.Key is IPropertySymbol) expression = "typeof(" + type + ").GetProperty(" + name + ", global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.DeclaredOnly)";
            else if (pair.Key is IEventSymbol) expression = "typeof(" + type + ").GetEvent(" + name + ", global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance)";
            else if (pair.Key is IMethodSymbol method) expression = "typeof(" + type + ").GetMethod(" + name + ", new global::System.Type[] { " + string.Join(", ", method.Parameters.Select(p => "typeof(" + p.Type.CSharpName() + ")")) + " })";
            else expression = "null";
            Writer.Line("private static readonly object? " + pair.Value + " = " + expression + ";");
        }
        foreach (var pair in _initSetters)
        {
            var method = pair.Key;
            Writer.Line("[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = " + CSharpNames.Literal(method.MetadataName) + ")]");
            Writer.Line("private static extern void " + pair.Value + "(" + (method.ContainingType.IsValueType ? "ref " : "") + method.ContainingType.CSharpName() + " target, " + method.Parameters[0].Type.CSharpName() + " value);");
        }
    }
    public void Error(string message, TextSpan span) => Diagnostics.Add(new("XG1200", message, span));
}
