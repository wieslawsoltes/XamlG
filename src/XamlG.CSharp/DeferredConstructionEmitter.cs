using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares a deferred object and its scalar-parameter markup assignment as ordinary typed C#.</summary>
internal sealed class DeferredConstructionEmitter(EmissionContext context, SourceInfoEmitter source, ObjectEmitter objects)
{
    private readonly Dictionary<string, (string Name, string ReturnType, ConstructionParameters Parameters)> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<ISymbol, string> _symbols = new(SymbolEqualityComparer.Default);

    public bool TryEmit(BoundDeferredExpression deferred, string incoming)
    {
        if (!deferred.UsesFunctionPointer || context.Document.Runtime.SourceInfo != null ||
            deferred.Content is not BoundObjectExpression { Object: var root } || !Node(root) ||
            root.Constructor!.Parameters.Length != 0 || root.Assignments.Length != 1 ||
            root.Assignments[0] is not BoundAdaptedSetAssignment
            {
                RuntimeDispatcher: null,
                Member: { Kind: BoundMemberKind.Property, Getter: not null, Setter.IsInitOnly: false, StaticSetter: null,
                    TargetDescriptor: BoundStaticExpression { GeneratedMemberName: null } descriptor },
                Value: BoundMarkupExpression markup
            } set || !Node(markup.Extension) || markup.Method.IsStatic || markup.Method.Parameters.Length > 1 ||
            markup.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None)) return false;
        var extension = markup.Extension;
        var parameters = new List<(BoundConstantExpression Value, ITypeSymbol Type)>();
        for (var index = 0; index < extension.Arguments.Length; index++)
        {
            if (extension.Arguments[index] is not BoundConstantExpression value || !Constant(value, extension.Constructor!.Parameters[index].Type)) return false;
            parameters.Add((value, extension.Constructor!.Parameters[index].Type));
        }
        foreach (var assignment in extension.Assignments)
        {
            if (assignment is not BoundSetAssignment
                {
                    RegisterName: false,
                    Member: { Kind: BoundMemberKind.Property, Getter: not null, Setter.IsInitOnly: false,
                        StaticSetter: null, TargetDescriptor: null },
                    Value: BoundConstantExpression value
                } property || !Constant(value, property.Member.ValueType)) return false;
            parameters.Add((value, property.Member.ValueType));
        }
        var values = new ConstructionParameters(new[] { root, extension }, parameters.ToArray());
        var returnType = deferred.FactoryReturnType?.CSharpName() ?? "object";
        // Include every operation whose implementation is shared. Source locations,
        // node keys and scalar values vary only through typed parameters.
        var key = string.Join("\0", new[] { Symbol(root.Constructor!), root.SupportsInitialize.ToString(),
            PropertyAccessor.Create(root.Type, set.Member).Key, Symbol(descriptor.Member), Symbol(set.Adapter), set.OwnAdapterResult.ToString(),
            returnType, Symbol(markup.Method), Symbol(extension.Constructor!), extension.SupportsInitialize.ToString() }
            .Concat(new[] { "adapted-types" }).Concat(set.AdaptedTypes.Select(Symbol))
            .Concat(new[] { "extension-properties" })
            .Concat(extension.Assignments.Cast<BoundSetAssignment>().Select(assignment => PropertyAccessor.Create(extension.Type, assignment.Member).Key)));
        if (!_factories.TryGetValue(key, out var factory))
        {
            factory = ("__XamlGBuildObject_" + context.Id + "_" + _factories.Count, returnType, values);
            _factories.Add(key, factory);
        }
        var call = "return " + factory.Name + "(" + incoming;
        foreach (var node in values.Nodes) call += ", " + CSharpNames.Literal(node.Key) + ", " + source.Index(node);
        var mappings = new List<(int Start, int Length, BoundConstantExpression Value)>();
        foreach (var (value, _) in values.Values)
        {
            var literal = CSharpNames.Constant(value.Value);
            call += ", ";
            var lineDirective = context.Document.Options.EmitLineDirectives && context.Document.Syntax.Path.Length != 0;
            if (lineDirective)
                call += "\n#line " + (context.Document.Syntax.Lines.GetPosition(Math.Min(value.Span.Start,
                    context.Document.Syntax.Text.Length)).Line + 1) + " " + CSharpNames.Literal(context.Document.Syntax.Path) + "\n";
            mappings.Add((call.Length, literal.Length, value));
            call += literal;
            if (lineDirective) call += "\n#line default\n";
        }
        call += ");";
        context.Writer.Line(call);
        var start = context.Writer.Position - call.Length - 1;
        foreach (var mapping in mappings)
            context.Mappings.Add(new(new(start + mapping.Start, mapping.Length), mapping.Value.Span, context.Document.Syntax.Path));
        return true;
    }

    private bool Node(BoundObject value) => !value.IsRoot && value.Name == null && value.Type.IsReferenceType &&
        value.FactoryMethod == null && value.Constructor != null && value.Constructor.Parameters.Length == value.Arguments.Length &&
        value.Constructor.Parameters.All(parameter => parameter.RefKind == RefKind.None) &&
        ReferenceEquals(value.Scope, context.Document.Root!.Scope);

    private static bool Constant(BoundConstantExpression value, ITypeSymbol type) => value.Value == null
        ? type.SpecialType is SpecialType.System_String or SpecialType.System_Object
        : ValueEmitter.ConstantType(value.Value) is var actual && actual != SpecialType.None &&
            (actual == type.SpecialType || actual == SpecialType.System_String && type.SpecialType == SpecialType.System_Object);

    private string Symbol(ISymbol symbol)
    {
        if (!_symbols.TryGetValue(symbol, out var key))
            _symbols.Add(symbol, key = symbol.ContainingAssembly.Identity + ":" + symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return key;
    }

    public void EmitHelpers()
    {
        foreach (var factory in _factories.Values)
        {
            var parameters = factory.Parameters;
            var declarations = parameters.Nodes.SelectMany((_, index) => new[] { "string __key" + index, "int __sourceIndex" + index })
                .Concat(parameters.Values.Select((value, index) => value.Type.CSharpName() + " __literal" + index));
            var writer = context.Writer;
            writer.Open("private static " + factory.ReturnType + " " + factory.Name + "(" + CSharpNames.Provider + "? __incoming, " + string.Join(", ", declarations) + ")");
            objects.EmitDeferredContext("__frame", string.Empty, "__incoming", functionPointer: true);
            writer.Open("try");
            writer.Line("var __ownerRoot = (" + context.Document.Root!.Type.CSharpName() + ")__frame.RootObject!;");
            var savedRoot = context.RootVariable;
            var savedParameters = context.ConstructionParameters;
            context.RootVariable = "__ownerRoot";
            context.ConstructionParameters = parameters;
            try
            {
                var value = objects.Emit(parameters.Nodes[0], "__frame", null, null);
                writer.Line(factory.ReturnType + " __result = " + value + ";");
                objects.Complete("__frame", "__result");
                writer.Line("return __result;");
            }
            finally { context.RootVariable = savedRoot; context.ConstructionParameters = savedParameters; }
            writer.Close(); ConstructionFailureEmitter.Emit(context, "__frame");
            writer.Close();
        }
    }
}
