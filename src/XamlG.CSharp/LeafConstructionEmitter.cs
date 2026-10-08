using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares typed construction shapes without moving ProvideValue or observable argument evaluation.</summary>
internal sealed class LeafConstructionEmitter(EmissionContext context, ValueEmitter values, SourceInfoEmitter source, ObjectEmitter objects)
{
    private readonly Dictionary<string, (string Name, BoundObject Object)> _factories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, string Construction, BoundMarkupExpression Markup)> _deferred = new(StringComparer.Ordinal);

    public bool CanShare(BoundObject value) => !value.IsRoot && value.Name == null && value.Type.IsReferenceType &&
        value.Assignments.All(CanShareAssignment) && value.FactoryMethod == null && value.Constructor != null &&
        value.Arguments.Length == value.Constructor.Parameters.Length &&
        value.Constructor.Parameters.All(parameter => parameter.RefKind == RefKind.None) &&
        context.Document.Runtime.SourceInfo == null;

    private bool CanShareAssignment(BoundAssignment assignment)
    {
        if (assignment is not BoundSetAssignment
            {
                RegisterName: false,
                Member: { Kind: BoundMemberKind.Property, Getter: not null, Setter.IsInitOnly: false,
                    StaticSetter: null, TargetDescriptor: null },
                Value: BoundConstantExpression constant
            } set) return false;
        // Moving a user conversion or boxing before construction changes its
        // observable order. Only exact scalar types and string/null references
        // are safe as helper parameters here.
        return CanPassConstant(constant, set.Member.ValueType);
    }

    private static bool CanPassConstant(BoundConstantExpression constant, ITypeSymbol parameter)
    {
        var target = parameter.SpecialType;
        return constant.Value == null ? target is SpecialType.System_String or SpecialType.System_Object :
            ValueEmitter.ConstantType(constant.Value) is var type && type != SpecialType.None &&
            (type == target || type == SpecialType.System_String && target == SpecialType.System_Object);
    }

    public string Emit(BoundObject value, string frame)
    {
        var name = Register(value);
        var arguments = values.EmitArguments(value.Constructor!, value.Arguments, frame);
        return EmitCall(value, name, frame, arguments, returnCall: false);
    }

    public bool TryEmitDeferred(BoundDeferredExpression deferred, string incoming)
    {
        if (!deferred.UsesFunctionPointer || deferred.Content is not BoundMarkupExpression markup ||
            !CanShare(markup.Extension) || !ReferenceEquals(markup.Extension.Scope, context.Document.Root!.Scope) ||
            markup.Method.IsStatic || markup.Method.Parameters.Length > 1 || markup.Method.Parameters.Any(parameter => parameter.RefKind != RefKind.None) ||
            !SymbolEqualityComparer.Default.Equals(markup.Method.ReturnType, deferred.FactoryReturnType) ||
            !markup.Extension.Arguments.Select((argument, index) => argument is BoundConstantExpression constant &&
                CanPassConstant(constant, markup.Extension.Constructor!.Parameters[index].Type)).All(safe => safe)) return false;
        var construction = Register(markup.Extension);
        var key = construction + "\0" + markup.Method.ContainingType.CSharpName() + "." + CSharpNames.Method(markup.Method) +
            "(" + string.Join(",", markup.Method.Parameters.Select(parameter => parameter.Type.CSharpName())) + ")";
        if (!_deferred.TryGetValue(key, out var factory))
        {
            factory = ("__XamlGBuildLeaf_" + context.Id + "_" + _deferred.Count, construction, markup);
            _deferred.Add(key, factory);
        }
        EmitCall(markup.Extension, factory.Name, incoming, markup.Extension.Arguments.Cast<BoundConstantExpression>()
            .Select(constant => CSharpNames.Constant(constant.Value)).ToArray(), returnCall: true);
        return true;
    }

    private string Register(BoundObject value)
    {
        var constructor = value.Constructor!;
        var key = value.Type.CSharpName() + "(" + string.Join(",", constructor.Parameters.Select(p => p.Type.CSharpName())) +
            "):" + value.SupportsInitialize + string.Concat(value.Assignments.Cast<BoundSetAssignment>()
                .Select(set => "\0" + PropertyAccessor.Create(value.Type, set.Member).Key));
        if (!_factories.TryGetValue(key, out var factory))
        {
            factory = ("__XamlGCreateLeaf_" + context.Id + "_" + _factories.Count, value);
            _factories.Add(key, factory);
        }
        return factory.Name;
    }

    private string EmitCall(BoundObject value, string factory, string frame, string[] arguments, bool returnCall)
    {
        var call = factory + "(" + frame + ", " + CSharpNames.Literal(value.Key) +
            ", " + source.Index(value) + (arguments.Length == 0 ? string.Empty : ", " + string.Join(", ", arguments));
        var mappings = new List<(int Offset, int Length, BoundAssignment Assignment)>();
        foreach (var assignment in value.Assignments.Cast<BoundSetAssignment>())
        {
            var literal = CSharpNames.Constant(((BoundConstantExpression)assignment.Value).Value);
            call += ", ";
            var lineDirective = context.Document.Options.EmitLineDirectives && context.Document.Syntax.Path.Length != 0;
            if (lineDirective)
                call += "\n#line " + (context.Document.Syntax.Lines.GetPosition(Math.Min(assignment.Span.Start,
                    context.Document.Syntax.Text.Length)).Line + 1) + " " + CSharpNames.Literal(context.Document.Syntax.Path) + "\n";
            mappings.Add((call.Length, literal.Length, assignment));
            call += literal;
            if (lineDirective) call += "\n#line default\n";
        }
        call += ")";
        var result = string.Empty;
        if (returnCall) context.Writer.Line("return " + call + ";");
        else result = context.Locals.Declare(value.Type.CSharpName(), call, "object", inferred: true);
        var start = context.Writer.Position - call.Length - 2; // The declaration ends with ;\n.
        foreach (var mapping in mappings)
            context.Mappings.Add(new(new(start + mapping.Offset, mapping.Length), mapping.Assignment.Span, context.Document.Syntax.Path));
        return result;
    }

    public void EmitHelpers()
    {
        foreach (var factory in _factories.Values)
        {
            var value = factory.Object;
            var parameters = value.Constructor!.Parameters;
            var declarations = parameters.Select((parameter, index) => parameter.Type.CSharpName() + " __argument" + index)
                .Concat(value.Assignments.Cast<BoundSetAssignment>().Select((set, index) => set.Member.ValueType.CSharpName() + " __property" + index)).ToArray();
            var arguments = parameters.Select((_, index) => "__argument" + index);
            var writer = context.Writer;
            writer.Open("private static " + value.Type.CSharpName() + " " + factory.Name + "(" + CSharpNames.Context +
                " __frame, string __key, int __sourceIndex" + (declarations.Length == 0 ? string.Empty : ", " + string.Join(", ", declarations)) + ")");
            writer.Line("var __value = new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ");");
            writer.Line((value.Assignments.IsEmpty ? string.Empty : "var __context = ") +
                "__frame.PushConstructed(__value, __key, " + context.SourceInfoTable + "[__sourceIndex]);");
            if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)__value).BeginInit();");
            for (var index = 0; index < value.Assignments.Length; index++)
            {
                var set = (BoundSetAssignment)value.Assignments[index];
                writer.Line(AssignmentEmitter.SetNonInit(set.Member, value.Type, "__value", "__property" + index) + ";");
                writer.Line(context.PropertyRegistration(PropertyAccessor.Create(value.Type, set.Member), "__context"));
            }
            if (value.SupportsInitialize) writer.Line("((global::System.ComponentModel.ISupportInitialize)__value).EndInit();");
            writer.Line("return __value;");
            writer.Close();
        }
        foreach (var factory in _deferred.Values)
        {
            var markup = factory.Markup;
            var value = markup.Extension;
            var declarations = value.Constructor!.Parameters.Select((parameter, index) => parameter.Type.CSharpName() + " __argument" + index)
                .Concat(value.Assignments.Cast<BoundSetAssignment>().Select((set, index) => set.Member.ValueType.CSharpName() + " __property" + index)).ToArray();
            var arguments = value.Arguments.Select((_, index) => "__argument" + index)
                .Concat(value.Assignments.Select((_, index) => "__property" + index)).ToArray();
            var writer = context.Writer;
            writer.Open("private static " + markup.Method.ReturnType.CSharpName() + " " + factory.Name + "(" + CSharpNames.Provider +
                "? __incoming, string __key, int __sourceIndex" + (declarations.Length == 0 ? string.Empty : ", " + string.Join(", ", declarations)) + ")");
            objects.EmitDeferredContext("__frame", string.Empty, "__incoming", functionPointer: true);
            writer.Open("try");
            writer.Line("var __ownerRoot = (" + context.Document.Root!.Type.CSharpName() + ")__frame.RootObject!;");
            writer.Line("var __value = " + factory.Construction + "(__frame, __key, __sourceIndex" +
                (arguments.Length == 0 ? string.Empty : ", " + string.Join(", ", arguments)) + ");");
            writer.Line(markup.Method.ReturnType.CSharpName() + " __result = ((" + markup.Method.ContainingType.CSharpName() +
                ")__value)." + CSharpNames.Method(markup.Method) + "(" + (markup.Method.Parameters.Length == 0 ? string.Empty : "__frame") + ");");
            objects.Complete("__frame", "__result");
            writer.Line("return __result;");
            writer.Close();
            ConstructionFailureEmitter.Emit(context, "__frame");
            writer.Close();
        }
    }
}
