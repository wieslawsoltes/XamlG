using Microsoft.CodeAnalysis;
using XamlG.Roslyn;
using XamlG.Syntax;
using System.Threading;
namespace XamlG.Compiler;

public sealed class BindingContext
{
    private readonly Dictionary<int, Dictionary<string, (ITypeSymbol Type, TextSpan Span)>> _names = new();
    private int _scopeCounter;
    private int _objectCounter;
    public BindingContext(XamlSyntaxTree syntax, RoslynTypeSystem types, XamlFrameworkProfile profile, XamlCompilerOptions options, CancellationToken cancellation)
    {
        Syntax = syntax; Types = types; Profile = profile; Options = options; Cancellation = cancellation;
        Values = new ValueBinder(this); Members = new MemberBinder(this); Objects = new ObjectBinder(this);
        Diagnostics.AddRange(syntax.Diagnostics);
        Runtime = RuntimeContractBinder.Bind(this);
    }
    public XamlSyntaxTree Syntax { get; }
    public RoslynTypeSystem Types { get; }
    public XamlFrameworkProfile Profile { get; }
    public BoundRuntimeConfiguration Runtime { get; }
    public XamlCompilerOptions Options { get; }
    public CancellationToken Cancellation { get; }
    public ValueBinder Values { get; }
    public MemberBinder Members { get; }
    public ObjectBinder Objects { get; }
    public INamedTypeSymbol? RootClass { get; set; }
    public List<XamlDiagnostic> Diagnostics { get; } = new();
    public List<BoundSymbolInfo> Symbols { get; } = new();
    public Stack<ObjectBindingBuilder> Ancestors { get; } = new();
    public XamlPropertyBindingScope? PropertyScope { get; internal set; }
    public XamlPropertyBindingScope EnterPropertyScope(ObjectBindingBuilder target, BoundMember member) => new(this, target, member);
    public IDisposable SuspendPropertyScope() => XamlPropertyBindingScope.Suspend(this);
    public int NewNameScope() => ++_scopeCounter;
    public string NewObjectKey() => "n" + _objectCounter++;
    public void Report(string code, string message, TextSpan span, XamlSeverity severity = XamlSeverity.Error) => Diagnostics.Add(new(code, message, span, severity));
    public void RegisterName(int scope, string name, ITypeSymbol type, TextSpan span)
    {
        if (!_names.TryGetValue(scope, out var names)) _names[scope] = names = new(StringComparer.Ordinal);
        if (names.ContainsKey(name)) Report("XG1012", $"Duplicate name '{name}' in the same name scope.", span);
        else names.Add(name, (type, span));
    }
    public ITypeSymbol? FindName(int scope, string name) => _names.TryGetValue(scope, out var names) && names.TryGetValue(name, out var definition) ? definition.Type : null;
    public TextSpan? FindNameSpan(int scope, string name) => _names.TryGetValue(scope, out var names) && names.TryGetValue(name, out var definition) ? definition.Span : null;
    public INamedTypeSymbol? ResolveType(string name, NamespaceScope scope, TextSpan span, string? typeArguments = null, bool report = true, bool extension = false, NamespaceScope? typeArgumentScope = null)
    {
        var parsed = XamlTypeNameParser.Parse(name, span, d => { if (report) Diagnostics.Add(d); });
        if (parsed == null) return null;
        if (typeArguments != null)
        {
            if (parsed.Arguments.Length != 0)
            { if (report) Report("XG1025", "Generic arguments cannot be specified both inline and in x:TypeArguments.", span); return null; }
            var explicitArguments = XamlTypeNameParser.ParseList(typeArguments, span, d => { if (report) Diagnostics.Add(d); });
            if (explicitArguments.Length == 0) return null;
            parsed = parsed with { Arguments = explicitArguments };
        }
        return ResolveType(parsed, scope, report, extension, typeArguments == null ? null : typeArgumentScope);
    }
    private INamedTypeSymbol? ResolveType(XamlTypeNameSyntax syntax, NamespaceScope scope, bool report, bool extension = false, NamespaceScope? typeArgumentScope = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        foreach (var rule in Profile.TypeBindingRules)
            if (rule.TryResolve(this, syntax, scope, out var resolved))
            {
                if (resolved != null) Symbols.Add(new(syntax.Span, resolved, "type"));
                return resolved;
            }
        var expanded = scope.Expand(syntax.Name);
        if (expanded.Namespace == null)
        { if (report) Report("XG1001", $"Namespace prefix for '{syntax.Name}' is not declared.", syntax.Span); return null; }
        var preferredName = extension ? expanded.LocalName + Types.Configuration.MarkupExtensionSuffix : expanded.LocalName;
        var fallbackName = extension ? expanded.LocalName : expanded.LocalName + Types.Configuration.MarkupExtensionSuffix;
        var resolution = Types.Resolve(expanded.Namespace, preferredName, syntax.Arguments.Length);
        if (resolution.Type == null && !resolution.IsAmbiguous)
            resolution = Types.Resolve(expanded.Namespace, fallbackName, syntax.Arguments.Length);
        if (resolution.Type == null)
        {
            if (report) Report(resolution.IsAmbiguous ? "XG1002" : "XG1003", resolution.IsAmbiguous ?
                $"Type '{syntax.Name}' is ambiguous: {string.Join(", ", resolution.Candidates.Select(t => t.ContainingAssembly.Name + ":" + t.ToDisplayString()))}." : $"Type '{syntax.Name}' was not found in namespace '{expanded.Namespace}'.", syntax.Span);
            return null;
        }
        var type = resolution.Type;
        if (syntax.Arguments.Length != 0)
        {
            var bound = new List<ITypeSymbol>();
            foreach (var argument in syntax.Arguments)
            {
                var item = ResolveType(argument, typeArgumentScope ?? scope, report);
                if (item == null) return null;
                bound.Add(item);
            }
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && (bound[0].IsReferenceType || bound[0] is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            { if (report) Report("XG1025", "Unable to construct generic type Nullable<T>: the type argument must be a non-nullable value type.", syntax.Span); return null; }
            if (!GenericConstraintValidator.Validate(this, type, bound, syntax.Span, report)) return null;
            try { type = type.Construct(bound.ToArray()); }
            catch (ArgumentException) { if (report) Report("XG1004", $"Invalid type arguments for '{syntax.Name}'.", syntax.Span); return null; }
        }
        if (syntax.Nullable)
        {
            if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            { if (report) Report("XG1025", "Unable to construct generic type Nullable<T>: the type argument must be a non-nullable value type; this type is already nullable.", syntax.Span); return null; }
            if (type.IsValueType) type = ((INamedTypeSymbol)Types.Special(SpecialType.System_Nullable_T)).Construct(type);
        }
        Symbols.Add(new(syntax.Span, type, "type"));
        return type;
    }
    public static IEnumerable<string> SplitTypeArguments(string text)
    {
        var depth = 0; var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == ',' && depth == 0) { yield return text.Substring(start, i - start).Trim(); start = i + 1; }
        }
        if (start < text.Length) yield return text.Substring(start).Trim();
    }
}
