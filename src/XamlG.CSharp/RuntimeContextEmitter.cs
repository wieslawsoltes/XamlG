using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;

namespace XamlG.CSharp;

internal sealed class RuntimeContextEmitter
{
    private readonly EmissionContext _context;
    private readonly NamespaceMapEmitter _namespaces;
    private readonly Dictionary<(bool Deferred, bool HasRoot), string> _factories = new();
    private bool _complete;
    private bool _nameScope;

    public RuntimeContextEmitter(EmissionContext context, NamespaceMapEmitter namespaces)
    {
        _context = context;
        _namespaces = namespaces;
    }

    public void Create(string variable, string outer, string root, bool deferred = false)
    {
        var key = (deferred, root != "null");
        if (!_factories.TryGetValue(key, out var factory))
            _factories.Add(key, factory = "__XamlGCreateContext_" + _context.Id + "_" + _factories.Count);
        _context.Writer.Line("var " + variable + " = " + factory + "(" + outer + ", " + root + ");");
        _context.SetFrameNamespaces(variable, _namespaces.GetMap(_context.Document.Root!.Scope));
    }

    private void CreateCore(string variable, string outer, string root, bool deferred)
    {
        var runtime = _context.Document.Runtime;
        var writer = _context.Writer;
        var rootLookup = outer;
        if (!deferred && runtime.RootServiceProviderFactory is { } rootFactory)
        {
            var provider = _context.Temporary("rootServices");
            writer.Line("var " + provider + " = " + rootFactory.ContainingType.CSharpName() + "." + CSharpNames.Method(rootFactory) + "(" + outer + ");");
            rootLookup = provider;
            var inherited = _context.Temporary("inheritedServices");
            writer.Line("var " + inherited + " = global::XamlG.Runtime.XamlServiceProviderChain.Combine(" + provider + ", " + outer + ");");
            outer = inherited;
        }
        var rootContract = runtime.Services.FirstOrDefault(s => s.Properties.Any(p => p.Value == XamlServiceValue.RootObject));
        if (root == "null" && rootContract != null)
        {
            var property = rootContract.Properties.First(p => p.Value == XamlServiceValue.RootObject).Property;
            root = "((" + rootContract.InterfaceType.CSharpName() + "?)" + rootLookup + "?.GetService(typeof(" + rootContract.InterfaceType.CSharpName() + ")))? .".Replace("? .", "?.") + CSharpNames.Identifier(property.Name);
        }
        var baseUri = _context.Document.Options.BaseUri is { } uri
            ? "new global::System.Uri(" + CSharpNames.Literal(uri) + ", global::System.UriKind.RelativeOrAbsolute)" : "null";
        var serviceChecks = runtime.Services.Select(s => "__type == typeof(" + s.InterfaceType.CSharpName() + ")").ToArray();
        var services = serviceChecks.Length == 0 ? "null" : "static (__frame, __type) => (" + string.Join(" || ", serviceChecks) + ") ? new " + _context.ServicesType + "(__frame) : null";
        var inner = runtime.InnerServiceProviderFactory is { } innerFactory
            ? "static __compiled => " + innerFactory.ContainingType.CSharpName() + "." + CSharpNames.Method(innerFactory) + "(__compiled)" : "null";
        var namespaces = _namespaces.GetMap(_context.Document.Root!.Scope);
        writer.Line("var " + variable + " = new " + CSharpNames.Context + "(" + outer + ", " + root + ", " + baseUri + ", " + services + ", " + inner + ", " + namespaces + ", useTypeDescriptorStubs: " + (_context.Document.Profile.Runtime.UseTypeDescriptorStubs ? "true" : "false") + ");");
        _context.SetFrameNamespaces(variable, namespaces);
        InitializeNameScope(variable, outer);
    }

    public void InitializeNameScope(string frame, string provider)
    {
        if (_context.Document.Runtime.NameScope == null) return;
        _nameScope = true;
        _context.Writer.Line("__XamlGInitializeNameScope_" + _context.Id + "(" + frame + ", " + provider + ");");
    }

    private void InitializeNameScopeCore(string frame, string provider)
    {
        if (_context.Document.Runtime.NameScope is not { } scope) return;
        var name = _context.Temporary("nameScope");
        var type = scope.ContractType.CSharpName();
        _context.Writer.Line("var " + name + " = (" + type + "?)" + provider + "?.GetService(typeof(" + type + ")) ?? new " + scope.ConcreteType.CSharpName() + "();");
        _context.Writer.Line(frame + ".AddService(typeof(" + type + "), " + name + ");");
    }

    public string Scope(string parent, NamespaceScope namespaces)
    {
        var map = _namespaces.GetMap(namespaces);
        if (map == "null" || _context.FrameNamespaces(parent) == map) return parent;
        var frame = _context.Temporary("scope");
        _context.Writer.Line("var " + frame + " = " + parent + ".WithNamespaces(" + map + ");");
        _context.SetFrameNamespaces(frame, map);
        return frame;
    }

    public void RegisterName(string frame, string nameExpression, string value)
    {
        if (_context.Document.Runtime.NameScope is { } scope)
            _context.Writer.Line("((" + scope.ContractType.CSharpName() + ")" + frame + ".GetService(typeof(" + scope.ContractType.CSharpName() + "))!)." + CSharpNames.Method(scope.Register) + "(" + nameExpression + ", " + value + ");");
        _context.Writer.Line(frame + ".RegisterName(" + nameExpression + ", " + value + ");");
    }

    public void Complete(string frame, string root)
    {
        _complete = true;
        _context.Writer.Line("__XamlGCompleteContext_" + _context.Id + "(" + frame + ", " + root + ");");
    }

    private void CompleteCore(string frame, string root)
    {
        _context.Writer.Line(frame + ".Complete(" + root + ");");
        if (_context.Document.Runtime.NameScope is not { } scope) return;
        var name = _context.Temporary("completedScope");
        _context.Writer.Line("var " + name + " = (" + scope.ContractType.CSharpName() + ")" + frame + ".GetService(typeof(" + scope.ContractType.CSharpName() + "))!;");
        _context.Writer.Line(name + "." + CSharpNames.Method(scope.Complete) + "();");
        if (scope.Attach is { } attach)
        {
            var target = _context.Temporary("scopeOwner");
            _context.Writer.Line("if ((object)" + root + " is " + attach.Parameters[0].Type.CSharpName() + " " + target + ") " + attach.ContainingType.CSharpName() + "." + CSharpNames.Method(attach) + "(" + target + ", " + name + ");");
        }
    }

    public void EmitHelpers()
    {
        var writer = _context.Writer;
        foreach (var factory in _factories)
        {
            writer.Open("private static " + CSharpNames.Context + " " + factory.Value + "(" + CSharpNames.Provider + "? __services, object? __root)");
            CreateCore("__created", "__services", factory.Key.HasRoot ? "__root" : "null", factory.Key.Deferred);
            writer.Line("return __created;");
            writer.Close();
        }
        if (_nameScope)
        {
            writer.Open("private static void __XamlGInitializeNameScope_" + _context.Id + "(" + CSharpNames.Context + " __frame, " + CSharpNames.Provider + "? __services)");
            InitializeNameScopeCore("__frame", "__services");
            writer.Close();
        }
        if (_complete)
        {
            writer.Open("private static void __XamlGCompleteContext_" + _context.Id + "(" + CSharpNames.Context + " __frame, object? __root)");
            CompleteCore("__frame", "__root");
            writer.Close();
        }
    }
}
