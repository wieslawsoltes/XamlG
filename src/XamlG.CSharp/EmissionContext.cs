using System.Threading;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
namespace XamlG.CSharp;
internal sealed class EmissionContext
{
    private int _temporary;
    private readonly Dictionary<ISymbol, string> _descriptors = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<IMethodSymbol, string> _initSetters = new(SymbolEqualityComparer.Default);
    public EmissionContext(BoundDocument document, CancellationToken cancellation)
    { Document = document; Cancellation = cancellation; Diagnostics.AddRange(document.Diagnostics); Id = CSharpNames.StableId(document.Options.DocumentId ?? document.Syntax.Path); }
    public BoundDocument Document { get; }
    public string Id { get; }
    public CancellationToken Cancellation { get; }
    private DynamicSetterEmitter? _dynamicSetters;
    public DynamicSetterEmitter DynamicSetters => _dynamicSetters ??= new(this);
    private DynamicAddEmitter? _dynamicAdds;
    public DynamicAddEmitter DynamicAdds => _dynamicAdds ??= new(this);
    public CSharpWriter Writer { get; } = new();
    public List<XamlDiagnostic> Diagnostics { get; } = new();
    public List<XamlSourceMapping> Mappings { get; } = new();
    public string RootVariable { get; set; } = "__root";
    public string ServicesType => "__XamlGServices_" + Id;
    public string Temporary(string role) => "__" + role + _temporary++;
    public void Map(TextSpan span, Action emit)
    {
        Cancellation.ThrowIfCancellationRequested();
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
    public void EmitMetadataHelpers()
    {
        foreach (var pair in _descriptors)
        {
            var type = pair.Key.ContainingType.CSharpName(); var name = CSharpNames.Literal(pair.Key.Name); string expression;
            if (pair.Key is IPropertySymbol) expression = "typeof(" + type + ").GetProperty(" + name + ", global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance)";
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
