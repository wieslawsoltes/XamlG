using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares repeated scalar setters, keeping descriptor evaluation, conversions and editing registration together.</summary>
internal sealed class LiteralAssignmentEmitter(EmissionContext context, ValueEmitter values)
{
    internal readonly record struct Key(IPropertySymbol Property, ITypeSymbol Input, ISymbol? Descriptor, string? GeneratedDescriptor);
    internal static readonly IEqualityComparer<Key> Keys = new KeyComparer();
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key x, Key y) => SymbolEqualityComparer.Default.Equals(x.Property, y.Property) &&
            SymbolEqualityComparer.Default.Equals(x.Input, y.Input) && SymbolEqualityComparer.Default.Equals(x.Descriptor, y.Descriptor) &&
            x.GeneratedDescriptor == y.GeneratedDescriptor;
        public int GetHashCode(Key value)
        {
            unchecked
            {
                var hash = SymbolEqualityComparer.Default.GetHashCode(value.Property);
                hash = hash * 397 ^ SymbolEqualityComparer.Default.GetHashCode(value.Input);
                hash = hash * 397 ^ (value.Descriptor == null ? 0 : SymbolEqualityComparer.Default.GetHashCode(value.Descriptor));
                return hash * 397 ^ (value.GeneratedDescriptor?.GetHashCode() ?? 0);
            }
        }
    }
    private sealed class Factory(Key key, BoundMember member)
    {
        public Key Key { get; } = key;
        public BoundMember Member { get; } = member;
        public int Count { get; set; }
        public string? Name { get; set; }
    }
    private readonly Dictionary<Key, Factory> _factories = new(Keys);
    private readonly List<Factory> _used = new();
    private bool _prepared;
    private bool _emitted;

    public bool TryEmit(BoundSetAssignment assignment, BoundObject owner, string target, string frame)
    {
        if (_emitted || !TryKey(assignment, owner, out var key)) return false;
        if (context.SharedProperties?.ScalarAssignments.TryGetValue(key, out var shared) == true)
        {
            var receiver = context.UsePropertyAliases ? shared.Alias : shared.Source.TypeName;
            context.Writer.Line(receiver + "." + shared.Method + "(" + target + ", " + frame + ", " + values.Emit(assignment.Value, frame) + ");");
            return true;
        }
        if (!_prepared) Prepare();
        if (!_factories.TryGetValue(key, out var factory) || factory.Count < 2) return false;
        if (factory.Name == null)
        {
            factory.Name = "__XamlGSetScalar_" + context.Id + "_" + _used.Count;
            _used.Add(factory);
        }
        context.Writer.Line(factory.Name + "(" + target + ", " + frame + ", " + values.Emit(assignment.Value, frame) + ");");
        return true;
    }

    private void Prepare()
    {
        _prepared = true;
        foreach (var owner in BoundTraversal.Objects(context.Document.Root!, includeDeferred: true))
        {
            context.Cancellation.ThrowIfCancellationRequested();
            foreach (var assignment in owner.Assignments.OfType<BoundSetAssignment>())
                if (TryKey(assignment, owner, out var key))
                {
                    if (!_factories.TryGetValue(key, out var factory)) _factories.Add(key, factory = new(key, assignment.Member));
                    factory.Count++;
                }
        }
    }

    internal static bool TryKey(BoundSetAssignment assignment, BoundObject owner, out Key key)
    {
        key = default;
        if (!owner.Type.IsReferenceType || assignment.RegisterName || assignment.Value is not BoundConstantExpression { Value: not null, Type: { } input } constant ||
            assignment.Member is not { Kind: BoundMemberKind.Property, StaticSetter: null, Symbol: IPropertySymbol { IsStatic: false, IsIndexer: false } property,
                Getter: { } getter, Setter: { IsInitOnly: false } setter } member ||
            member.Name != property.Name || !SymbolEqualityComparer.Default.Equals(member.ValueType, property.Type) ||
            !SymbolEqualityComparer.Default.Equals(getter, property.GetMethod) || !SymbolEqualityComparer.Default.Equals(setter, property.SetMethod) ||
            member.TargetDescriptor != null && member.TargetDescriptor is not BoundStaticExpression) return false;
        var scalar = ValueEmitter.ConstantType(constant.Value);
        if (scalar == SpecialType.None || scalar != input.SpecialType) return false;
        // A variable does not have C#'s implicit constant-expression conversions.
        // Keep narrowing, enum-zero and user-defined conversion selection inline.
        // Boxing and nullable wrapping still happen after descriptor evaluation.
        if (!SymbolEqualityComparer.Default.Equals(input, member.ValueType) && member.ValueType.SpecialType != SpecialType.System_Object &&
            !(member.ValueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
                SymbolEqualityComparer.Default.Equals(input, nullable.TypeArguments[0]))) return false;
        var descriptor = member.TargetDescriptor as BoundStaticExpression;
        key = new(property, input, descriptor?.Member, descriptor?.GeneratedMemberName);
        return true;
    }

    public void EmitHelpers()
    {
        _emitted = true;
        foreach (var factory in _used)
        {
            EmitHelper(context.Writer, factory.Key, factory.Member, factory.Name!, "private",
                context.PropertyRegistration(PropertyAccessor.Create(factory.Key.Property.ContainingType, factory.Member), "__frame"));
        }
    }

    internal static string StableKey(Key key, BoundMember member) =>
        PropertyAccessor.Create(key.Property.ContainingType, member).Key + "\0" + key.Input.CSharpName() + "\0" +
        (member.TargetDescriptor is BoundStaticExpression descriptor ? Descriptor(descriptor) : string.Empty);

    internal static void EmitHelper(CSharpWriter writer, Key key, BoundMember member, string name, string accessibility, string registration)
    {
        var receiver = key.Property.ContainingType;
        writer.Open(accessibility + " static void " + name + "(" + receiver.CSharpName() + " __target, " +
            CSharpNames.Context + " __frame, " + key.Input.CSharpName() + " __value)");
        if (member.TargetDescriptor is BoundStaticExpression descriptor)
            writer.Line("object? __descriptor = " + Descriptor(descriptor) + ";");
        writer.Line(AssignmentEmitter.SetNonInit(member, receiver, "__target", "__value") + ";");
        writer.Line(registration);
        writer.Close();
    }

    private static string Descriptor(BoundStaticExpression descriptor) => descriptor.Member.ContainingType.CSharpName() + "." +
        CSharpNames.Identifier(descriptor.GeneratedMemberName ?? descriptor.Member.Name);
}
