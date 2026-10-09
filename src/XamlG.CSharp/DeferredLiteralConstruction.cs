using System.Text;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed partial class DeferredConstructionEmitter
{
    // Parsed values already lowered to typed constructors/calls still need to
    // execute inside the deferred lifetime. Parameterize only exact literals;
    // descriptors, conversions, initialization and cleanup stay in the helper.
    private bool TryEmitLoweredObject(BoundDeferredExpression deferred, string incoming)
    {
        if (!deferred.UsesFunctionPointer || context.Document.Runtime.SourceInfo != null ||
            deferred.Content is not BoundObjectExpression { Object: var root } || !Node(root) ||
            root.Constructor!.Parameters.Length != 0 || root.Assignments.IsEmpty ||
            root.Assignments.Any(assignment => assignment is not BoundSetAssignment)) return false;
        var key = new StringBuilder("lowered-object(");
        key.Append(Symbol(root.Type)).Append(';').Append(root.SupportsInitialize).Append(';');
        var parameters = new List<(BoundConstantExpression Value, ITypeSymbol Type)>();
        foreach (var assignment in root.Assignments)
        {
            if (assignment is not BoundSetAssignment
                {
                    RegisterName: false,
                    Member: { Kind: BoundMemberKind.Property, Getter: not null, Setter.IsInitOnly: false,
                        StaticSetter: null }
                } set) return false;
            Text(key, PropertyAccessor.Create(root.Type, set.Member).Key);
            if (set.Member.TargetDescriptor is { } descriptor)
            {
                key.Append("descriptor(");
                if (descriptor is not BoundStaticExpression { GeneratedMemberName: null } ||
                    !LoweredValue(descriptor, descriptor.Type, key, parameters)) return false;
                key.Append(')');
            }
            else key.Append("no-descriptor;");
            if (!LoweredValue(set.Value, set.Member.ValueType, key, parameters)) return false;
        }
        var returnType = deferred.FactoryReturnType?.CSharpName() ?? "object";
        key.Append(')'); Text(key, returnType);
        return EmitFactoryCall(key.ToString(), returnType, new(new[] { root }, parameters.ToArray()), incoming);
    }

    private static void Text(StringBuilder key, string value) => key.Append(value.Length).Append(':').Append(value);

    private bool LoweredValue(BoundExpression value, ITypeSymbol? expected, StringBuilder key,
        List<(BoundConstantExpression Value, ITypeSymbol Type)> parameters)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (value.Type is { ContainingAssembly: null }) return false;
        Text(key, value.Type == null ? string.Empty : Symbol(value.Type));
        switch (value)
        {
            case BoundConstantExpression constant:
                // Keep constant-expression narrowing and overload selection in
                // the body. An ordinary parameter has different C# conversions.
                var actual = ValueEmitter.ConstantType(constant.Value);
                if (actual != SpecialType.None && constant.Type?.SpecialType == actual &&
                    (expected?.SpecialType == actual || expected?.SpecialType == SpecialType.System_Object))
                {
                    var index = parameters.FindIndex(parameter => ReferenceEquals(parameter.Value, constant));
                    if (index < 0) { index = parameters.Count; parameters.Add((constant, constant.Type)); }
                    // Shared bound constants must keep their aliasing pattern:
                    // the emitter substitutes by identity, not structural equality.
                    key.Append("parameter:").Append(index).Append(';');
                }
                else { key.Append("constant:"); Text(key, CSharpNames.Constant(constant.Value)); }
                return true;
            case BoundStaticExpression { GeneratedMemberName: null } member:
                key.Append("static:"); Text(key, Symbol(member.Member)); return true;
            case BoundEnumExpression enumeration:
                key.Append("enum(");
                foreach (var field in enumeration.Fields) Text(key, Symbol(field));
                key.Append(')'); return true;
            case BoundTypeExpression type when type.ReferencedType.ContainingAssembly != null:
                key.Append("type:"); Text(key, Symbol(type.ReferencedType)); return true;
            case BoundCastExpression cast:
                key.Append("cast(");
                if (!LoweredValue(cast.Value, cast.TargetType, key, parameters)) return false;
                key.Append(')'); return true;
            case BoundNewExpression creation when creation.Initializers.IsEmpty:
                if (creation.Constructor.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
                    creation.Arguments.Length != creation.Constructor.Parameters.Length) return false;
                key.Append("new("); Text(key, Symbol(creation.Constructor));
                for (var index = 0; index < creation.Arguments.Length; index++)
                    if (!LoweredValue(creation.Arguments[index], creation.Constructor.Parameters[index].Type, key, parameters)) return false;
                key.Append(')'); return true;
            case BoundCallExpression { Receiver: null, RuntimeDependency: null } call when call.Method.IsStatic:
                if (call.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
                    call.Arguments.Length != call.Method.Parameters.Length) return false;
                key.Append("call("); Text(key, Symbol(call.Method));
                for (var index = 0; index < call.Arguments.Length; index++)
                    if (!LoweredValue(call.Arguments[index], call.Method.Parameters[index].Type, key, parameters)) return false;
                key.Append(')'); return true;
            default: return false;
        }
    }
}
