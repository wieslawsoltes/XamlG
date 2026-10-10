using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares repeated, statically typed markup assignments. Only side-effect-free
/// scalar values cross the helper boundary; observable operations use the normal emitter.</summary>
internal sealed class MarkupAssignmentEmitter(EmissionContext context, ValueEmitter values)
{
    private sealed class Factory(BoundSetAssignment assignment, BoundObject owner, ConstructionParameters parameters)
    {
        public BoundSetAssignment Assignment { get; } = assignment;
        public BoundObject Owner { get; } = owner;
        public ConstructionParameters Parameters { get; } = parameters;
        public int Count { get; set; }
        public string? Name { get; set; }
        public string? NamespaceMap { get; set; }
    }

    private sealed record Occurrence(Factory Factory, ConstructionParameters Parameters);
    private readonly Dictionary<(BoundObject Owner, BoundSetAssignment Assignment), Occurrence> _occurrences = new(Identity.Instance);
    private readonly Dictionary<ISymbol, string> _symbols = new(SymbolEqualityComparer.Default);
    private readonly List<Factory> _used = new();
    private bool _prepared;
    private bool _emitting;

    public bool TryEmit(BoundSetAssignment assignment, BoundObject owner, string target, string frame)
    {
        if (_emitting || !context.Document.Options.ShareMarkupAssignments || context.ConstructionParameters != null || context.Document.Runtime.SourceInfo != null ||
            Unwrap(assignment.Value) is not BoundMarkupExpression) return false;
        if (!_prepared) Prepare();
        if (!_occurrences.TryGetValue((owner, assignment), out var occurrence) || occurrence.Factory.Count < 2) return false;
        var factory = occurrence.Factory;
        var map = context.FrameNamespaces(frame);
        if (factory.Name != null && factory.NamespaceMap != map) return false;
        if (factory.Name == null)
        {
            factory.Name = "__XamlGAssignMarkup_" + context.Id + "_" + _used.Count;
            factory.NamespaceMap = map;
            _used.Add(factory);
        }

        // ForTarget evaluates its descriptor before construction. Keep that evaluation
        // at the call site, before the pure scalar parameters, with the same boxing.
        var descriptor = assignment.Member.TargetDescriptor is { } expression
            ? values.Emit(expression, frame) : context.Descriptor(assignment.Member);
        var node = occurrence.Parameters.Nodes[0];
        var sourceIndex = new SourceInfoEmitter(context).Index(node);
        var call = new StringBuilder(factory.Name).Append('(').Append(target).Append(", ").Append(frame)
            .Append(", ").Append(descriptor).Append(", ").Append(CSharpNames.Literal(node.Key))
            .Append(", ").Append(sourceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var mappings = new List<(int Offset, int Length, BoundConstantExpression Value)>();
        foreach (var (value, _) in occurrence.Parameters.Values)
        {
            call.Append(", ");
            var directives = context.Document.Options.EmitLineDirectives && context.Document.Syntax.Path.Length != 0;
            if (directives)
                call.Append("\n#line ").Append((context.Document.Syntax.Lines.GetPosition(Math.Min(value.Span.Start,
                    context.Document.Syntax.Text.Length)).Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(' ').Append(CSharpNames.Literal(context.Document.Syntax.Path)).Append('\n');
            var literal = CSharpNames.Constant(value.Value);
            mappings.Add((call.Length, literal.Length, value));
            call.Append(literal);
            if (directives) call.Append("\n#line default\n");
        }
        call.Append(");");
        context.Writer.Line(call.ToString());
        var start = context.Writer.Position - call.Length - 1;
        foreach (var mapping in mappings)
            context.Mappings.Add(new(new(start + mapping.Offset, mapping.Length), mapping.Value.Span, context.Document.Syntax.Path));
        return true;
    }

    private void Prepare()
    {
        _prepared = true;
        var factories = new Dictionary<string, Factory>(StringComparer.Ordinal);
        foreach (var owner in BoundTraversal.Objects(context.Document.Root!, includeDeferred: true))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            foreach (var assignment in owner.Assignments)
            {
                if (assignment is not BoundSetAssignment set || !TryAnalyze(set, owner, out var parameters, out var key)) continue;
                if (!factories.TryGetValue(key, out var factory))
                    factories.Add(key, factory = new(set, owner, parameters));
                factory.Count++;
                // Bound DAGs may visit an assignment more than once. Store by reference,
                // never invoke the recursive equality/hash implementation of IR records.
                _occurrences[(owner, set)] = new(factory, parameters);
            }
        }
    }

    private bool TryAnalyze(BoundSetAssignment assignment, BoundObject owner, out ConstructionParameters parameters, out string key)
    {
        parameters = null!;
        key = string.Empty;
        if (assignment.RegisterName || owner.Type.TypeKind != TypeKind.Class || !owner.Type.IsReferenceType || !ReferenceEquals(owner.Scope, context.Document.Root!.Scope) ||
            assignment.Member is not { Kind: BoundMemberKind.Property, StaticSetter: null,
                Symbol: IPropertySymbol { IsStatic: false, IsIndexer: false } property,
                Getter: { } getter, Setter: { IsInitOnly: false } setter } member ||
            member.Name != property.Name || !SymbolEqualityComparer.Default.Equals(member.ValueType, property.Type) ||
            !SymbolEqualityComparer.Default.Equals(getter, property.GetMethod) || !SymbolEqualityComparer.Default.Equals(setter, property.SetMethod) ||
            member.TargetDescriptor != null && member.TargetDescriptor is not BoundStaticExpression ||
            Unwrap(assignment.Value) is not BoundMarkupExpression markup ||
            markup.Method.IsStatic || markup.Method.ReturnsVoid || markup.Method.ReturnsByRef || markup.Method.ReturnsByRefReadonly ||
            markup.Method.Parameters.Length > 1 || markup.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None)) return false;
        var extension = markup.Extension;
        if (extension.IsRoot || extension.Name != null || !extension.Type.IsReferenceType || extension.FactoryMethod != null ||
            extension.Constructor == null || extension.Arguments.Length != extension.Constructor.Parameters.Length ||
            !ReferenceEquals(extension.Scope, owner.Scope) || extension.Constructor.Parameters.Any(parameter => parameter.RefKind != RefKind.None)) return false;
        var arguments = new List<(BoundConstantExpression Value, ITypeSymbol Type)>();
        for (var i = 0; i < extension.Arguments.Length; i++)
        {
            if (extension.Arguments[i] is not BoundConstantExpression constant ||
                !CanPass(constant, extension.Constructor.Parameters[i].Type)) return false;
            arguments.Add((constant, extension.Constructor.Parameters[i].Type));
        }
        foreach (var item in extension.Assignments)
        {
            if (item is not BoundSetAssignment { RegisterName: false,
                Member: { Kind: BoundMemberKind.Property,
                    Symbol: IPropertySymbol { IsStatic: false, IsIndexer: false } extensionProperty,
                    Getter: { } extensionGetter, Setter: { IsInitOnly: false } extensionSetter,
                    StaticSetter: null, TargetDescriptor: null }, Value: BoundConstantExpression constant } set ||
                set.Member.Name != extensionProperty.Name ||
                !SymbolEqualityComparer.Default.Equals(set.Member.ValueType, extensionProperty.Type) ||
                !SymbolEqualityComparer.Default.Equals(extensionGetter, extensionProperty.GetMethod) ||
                !SymbolEqualityComparer.Default.Equals(extensionSetter, extensionProperty.SetMethod) ||
                !CanPass(constant, set.Member.ValueType)) return false;
            arguments.Add((constant, set.Member.ValueType));
        }
        var signature = new StringBuilder(owner.Type.CSharpName()).Append('\0').Append(PropertyAccessor.Create(owner.Type, member).Key)
            .Append('\0').Append(Symbol(extension.Constructor)).Append('\0').Append(Symbol(markup.Method))
            .Append('\0').Append(extension.SupportsInitialize);
        for (var value = assignment.Value; value is BoundCastExpression cast; value = cast.Value)
            signature.Append("\0cast:").Append(cast.TargetType.CSharpName());
        foreach (var item in extension.Assignments.Cast<BoundSetAssignment>())
            signature.Append("\0property:").Append(PropertyAccessor.Create(extension.Type, item.Member).Key);
        parameters = new(new[] { extension }, arguments.ToArray());
        key = signature.ToString();
        return true;
    }

    private static BoundExpression Unwrap(BoundExpression value)
    {
        while (value is BoundCastExpression cast) value = cast.Value;
        return value;
    }

    // Do not move boxing, narrowing, user conversions or service-dependent values
    // ahead of the constructor. Strings and null reference conversions are inert.
    private static bool CanPass(BoundConstantExpression value, ITypeSymbol parameter) => value.Value == null
        ? parameter.SpecialType is SpecialType.System_String or SpecialType.System_Object
        : ValueEmitter.ConstantType(value.Value) is var type && type != SpecialType.None &&
            (type == parameter.SpecialType || type == SpecialType.System_String && parameter.SpecialType == SpecialType.System_Object);

    private string Symbol(ISymbol symbol)
    {
        if (!_symbols.TryGetValue(symbol, out var key))
            _symbols.Add(symbol, key = symbol.ContainingAssembly.Identity + ":" + symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        return key;
    }

    public void EmitHelpers(Action<BoundAssignment, BoundObject, string, string> emit)
    {
        _emitting = true;
        foreach (var factory in _used)
        {
            var writer = context.Writer;
            var parameters = factory.Parameters;
            var declarations = parameters.Values.Select((value, index) => value.Type.CSharpName() + " __literal" + index).ToArray();
            writer.Open("private static void " + factory.Name + "(" + factory.Owner.Type.CSharpName() + " __target, " +
                CSharpNames.Context + " __frame, object? __descriptor, string __key0, int __sourceIndex0" +
                (declarations.Length == 0 ? string.Empty : ", " + string.Join(", ", declarations)) + ")");
            if (factory.NamespaceMap != null) context.SetFrameNamespaces("__frame", factory.NamespaceMap);
            else context.InheritFrameNamespaces("__frame", string.Empty);
            var saved = context.ConstructionParameters;
            context.ConstructionParameters = parameters;
            try
            {
                var member = factory.Assignment.Member with { TargetDescriptor = new BoundParameterExpression("__descriptor",
                    ObjectType(factory.Owner.Type), factory.Assignment.Member.Span) };
                emit(factory.Assignment with { Member = member }, factory.Owner, "__target", "__frame");
            }
            finally { context.ConstructionParameters = saved; }
            writer.Close();
        }
    }

    private static INamedTypeSymbol ObjectType(INamedTypeSymbol type)
    {
        while (type.BaseType != null) type = type.BaseType;
        return type;
    }

    private sealed class Identity : IEqualityComparer<(BoundObject Owner, BoundSetAssignment Assignment)>
    {
        public static readonly Identity Instance = new();
        public bool Equals((BoundObject Owner, BoundSetAssignment Assignment) left, (BoundObject Owner, BoundSetAssignment Assignment) right) =>
            ReferenceEquals(left.Owner, right.Owner) && ReferenceEquals(left.Assignment, right.Assignment);
        public int GetHashCode((BoundObject Owner, BoundSetAssignment Assignment) value) =>
            unchecked(RuntimeHelpers.GetHashCode(value.Owner) * 397 ^ RuntimeHelpers.GetHashCode(value.Assignment));
    }
}
