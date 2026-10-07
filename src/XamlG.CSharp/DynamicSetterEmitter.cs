using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>
/// Interns ordered runtime dispatch plans. Build/Populate and repeated properties share
/// one strongly typed helper instead of duplicating the same dispatch ladder.
/// </summary>
internal sealed class DynamicSetterEmitter
{
    private readonly EmissionContext _context;
    private readonly Dictionary<string, (string Name, INamedTypeSymbol Target, ImmutableArray<BoundValueSetter> Setters)> _plans = new(StringComparer.Ordinal);

    public DynamicSetterEmitter(EmissionContext context) => _context = context;

    public string Register(INamedTypeSymbol target, ImmutableArray<BoundValueSetter> setters)
    {
        var key = target.CSharpName() + "\n" + string.Join("\n", setters.Select(Describe));
        if (_plans.TryGetValue(key, out var existing)) return existing.Name;
        var name = "__XamlDynamicSet_" + CSharpNames.StableId(key);
        _plans.Add(key, (name, target, setters));
        return name;
    }

    public void Emit()
    {
        var writer = _context.Writer;
        foreach (var plan in _plans.Values)
        {
            _context.Cancellation.ThrowIfCancellationRequested();
            writer.Open("private static void " + plan.Name + "(" + plan.Target.CSharpName() + " __target, object? __value)");
            BoundValueSetter? acceptsNull = null;
            var catchAll = false;
            for (var i = 0; i < plan.Setters.Length; i++)
            {
                var setter = plan.Setters[i];
                if (setter.AllowRuntimeNull) acceptsNull ??= setter;
                var patternType = setter.ValueType is INamedTypeSymbol nullable &&
                    nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                        ? nullable.TypeArguments[0] : setter.ValueType;
                var condition = "__value is " + patternType.CSharpName();
                catchAll = setter.ValueType.SpecialType == SpecialType.System_Object && setter.AllowRuntimeNull;
                if (catchAll) condition = "true";
                writer.Open((i == 0 ? "if" : "else if") + " (" + condition + ")");
                Call(setter, plan.Target);
                writer.Close();
                if (catchAll) break;
            }
            if (!catchAll)
            {
                writer.Open(plan.Setters.Length == 0 ? string.Empty : "else");
                writer.Open("if (__value is null)");
                if (acceptsNull != null) Call(acceptsNull, plan.Target);
                else writer.Line("throw new global::System.NullReferenceException(\"A null XAML value cannot be unboxed into a non-nullable setter.\");");
                writer.Close();
                writer.Line("throw new global::System.InvalidCastException(\"No XAML setter accepts the runtime value.\");");
                writer.Close();
            }
            writer.Close();
        }

        void Call(BoundValueSetter setter, ITypeSymbol targetType)
        {
            writer.Line("var __typed = (" + setter.ValueType.CSharpName() + ")__value!;");
            EmitSetter(setter, targetType);
            writer.Line("return;");
        }
    }

    private void EmitSetter(BoundValueSetter setter, ITypeSymbol targetType)
    {
        var writer = _context.Writer;
        if (setter is BoundPropertyValueSetter property)
        {
            var member = property.Member;
            if (member.Setter?.IsInitOnly == true)
                writer.Line(_context.InitSetter(member.Setter) + "(__target, __typed);");
            else if (member.Kind == BoundMemberKind.AttachedProperty)
                writer.Line(member.Setter!.ContainingType.CSharpName() + "." + CSharpNames.Method(member.Setter) + "(__target, __typed);");
            else
                writer.Line(CSharpNames.MemberTarget(member.Symbol, targetType, "__target") + "." + CSharpNames.Identifier(member.Name) + " = __typed;");
            return;
        }
        if (setter is BoundMethodValueSetter method)
        {
            var receiver = "__target";
            foreach (var receiverProperty in method.ReceiverPath)
                receiver += "." + CSharpNames.Identifier(receiverProperty.Name);
            var callTarget = method.Method.IsStatic ? method.Method.ContainingType.CSharpName()
                : "((" + method.Method.ContainingType.CSharpName() + ")" + receiver + ")";
            writer.Line(callTarget + "." + CSharpNames.Method(method.Method) + "(" +
                (method.IncludeTarget ? "__target, " : string.Empty) + "__typed);");
            return;
        }
        if (setter is BoundCollectionValueSetter collection)
        {
            var receiver = AssignmentEmitter.Get(collection.Collection, targetType, "__target");
            writer.Line("((" + collection.AddMethod.ContainingType.CSharpName() + ")" + receiver + ")." + CSharpNames.Method(collection.AddMethod) + "(__typed);");
            return;
        }
        throw new InvalidOperationException("Unknown bound dynamic setter: " + setter.GetType().Name);
    }

    private static string Describe(BoundValueSetter setter) => setter switch
    {
        BoundPropertyValueSetter property => "property:" + property.Member.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + setter.AllowRuntimeNull,
        BoundMethodValueSetter method => "method:" + method.Method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + method.IncludeTarget + ":" + setter.AllowRuntimeNull + ":" + string.Join(".", method.ReceiverPath.Select(p => p.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))),
        BoundCollectionValueSetter collection => "collection:" + collection.Collection.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + collection.AddMethod.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + setter.AllowRuntimeNull,
        _ => throw new InvalidOperationException("Unknown bound dynamic setter: " + setter.GetType().Name)
    };
}
