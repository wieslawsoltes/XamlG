using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;

namespace XamlG.CSharp;

internal sealed class ValueEmitter
{
    private readonly EmissionContext _context;
    private readonly ObjectEmitter _objects;
    public ValueEmitter(EmissionContext context, ObjectEmitter objects) { _context = context; _objects = objects; }

    public string Emit(BoundExpression value, string frame)
    {
        _context.Cancellation.ThrowIfCancellationRequested();
        switch (value)
        {
            case BoundChoiceExpression choice: return new ChoiceExpressionEmitter(_context, _objects, this).Emit(choice, frame);
            case BoundResourceExpression resource: return new ResourceExpressionEmitter(_context).Emit(resource, frame);
            case BoundConstantExpression constant: return CSharpNames.Constant(constant.Value);
            case BoundEnumExpression enumeration:
                return "(" + string.Join(" | ", enumeration.Fields.Select(f => f.ContainingType.CSharpName() + "." + CSharpNames.Identifier(f.Name))) + ")";
            case BoundCastExpression cast: return "((" + cast.TargetType.CSharpName() + ")(" + Emit(cast.Value, frame) + "))";
            case BoundTypeExpression type: return "typeof(" + type.ReferencedType.CSharpName() + ")";
            case BoundMethodHandleExpression handle:
                return "typeof(" + handle.Method.ContainingType.CSharpName() + ").GetMethod(" + CSharpNames.Literal(handle.Method.Name) +
                    ", global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Static | global::System.Reflection.BindingFlags.DeclaredOnly, null, new global::System.Type[] { " +
                    string.Join(", ", handle.Method.Parameters.Select(parameter => "typeof(" + parameter.Type.CSharpName() + ")")) + " }, null)!.MethodHandle";
            case BoundStaticExpression field: return field.Member.ContainingType.CSharpName() + "." + CSharpNames.Identifier(field.Member.Name);
            case BoundParameterExpression or BoundLambdaExpression or BoundPropertyAccessExpression or BoundFieldAccessExpression or BoundAssignmentExpression or BoundMethodGroupExpression:
                return new FunctionalExpressionEmitter(_context, this).Emit(value, frame);
            case BoundServiceExpression service:
                return service.ServiceType.HasMetadataName(ClrNames.IServiceProvider)
                    ? frame : "((" + service.ServiceType.CSharpName() + ")" + frame + ".GetService(typeof(" + service.ServiceType.CSharpName() + "))!)";
            case BoundReferenceExpression reference:
                return frame + ".ResolveName<" + (reference.Type?.CSharpName() ?? "object") + ">(" + CSharpNames.Literal(reference.Name) + ")";
            case BoundObjectExpression obj: return _objects.Emit(obj.Object, frame, null, null);
            case BoundArrayExpression array:
                var arrayLocal = _context.Temporary("array");
                _context.Writer.Line("var " + arrayLocal + " = " + ArrayCreation(array) + ";");
                for (var index = 0; index < array.Values.Length; index++)
                    _context.Writer.Line(arrayLocal + "[" + index + "] = " + Emit(array.Values[index], frame) + ";");
                return arrayLocal;
            case BoundCollectionExpression collection:
                var collectionLocal = _context.Temporary("collection");
                _context.Writer.Line("var " + collectionLocal + " = new " + collection.Constructor.ContainingType.CSharpName() + "();");
                if (collection.Capacity.SetMethod!.IsInitOnly)
                    _context.Writer.Line(_context.InitSetter(collection.Capacity.SetMethod) + "(" + collectionLocal + ", " + collection.Values.Length + ");");
                else
                    _context.Writer.Line("((" + collection.Capacity.ContainingType.CSharpName() + ")" + collectionLocal + ")." + CSharpNames.Identifier(collection.Capacity.Name) + " = " + collection.Values.Length + ";");
                foreach (var item in collection.Values)
                {
                    var itemValue = Emit(item, frame);
                    _context.Writer.Line("((" + collection.AddMethod.ContainingType.CSharpName() + ")" + collectionLocal + ")." + CSharpNames.Method(collection.AddMethod) +
                        "((" + collection.AddMethod.Parameters[0].Type.CSharpName() + ")(" + itemValue + "));");
                }
                return collectionLocal;
            case BoundNewExpression creation:
                var constructed = "new " + creation.Constructor.ContainingType.CSharpName() + "(" + string.Join(", ", EmitArguments(creation.Constructor, creation.Arguments, frame)) + ")";
                if (creation.SuppressSourceInfo || creation.SourceInfoSpan is not { } sourceSpan || creation.Constructor.ContainingType.IsValueType || _context.Document.Runtime.SourceInfo == null) return constructed;
                var located = _context.Temporary("literal");
                _context.Writer.Line("var " + located + " = " + constructed + ";");
                new SourceInfoEmitter(_context).EmitConstructed(located, sourceSpan);
                return located;
            case BoundCallExpression call:
                var receiver = call.Method.IsStatic ? call.Method.ContainingType.CSharpName() : call.Receiver == null ? _context.RootVariable : "(" + Emit(call.Receiver, frame) + ")";
                if (!call.Method.IsStatic && call.Receiver != null && !call.Arguments.IsEmpty)
                {
                    var local = _context.Temporary("receiver");
                    _context.Writer.Line((call.Receiver.Type ?? call.Method.ContainingType).CSharpName() + " " + local + " = " + receiver + ";");
                    receiver = local;
                }
                return receiver + "." + CSharpNames.Method(call.Method) + "(" + string.Join(", ", EmitArguments(call.Method, call.Arguments, frame)) + ")";
            case BoundParseExpression parse:
                return parse.Method.ContainingType.CSharpName() + "." + CSharpNames.Method(parse.Method) + "(" + CSharpNames.Literal(parse.Text) +
                    (parse.Method.Parameters.Length == 2 ? ", (" + parse.Method.Parameters[1].Type.CSharpName() + ")" + CSharpNames.InvariantCulture : string.Empty) + ")";
            case BoundConverterExpression converter:
                return Convert(converter, converter.Converter, converter.ValueType, frame, () => CSharpNames.Literal(converter.Text));
            case BoundValueConverterExpression converter:
                return Convert(converter, converter.Converter, converter.ValueType, frame, () => Emit(converter.Value, frame));
            case BoundMarkupExpression markup:
                var extension = _objects.Emit(markup.Extension, frame, null, null);
                return extension + "." + CSharpNames.Method(markup.Method) + "(" + (markup.Method.Parameters.Length == 0 ? string.Empty : frame) + ")";
            case BoundDeferredExpression deferred: return Deferred(deferred, frame);
            case BoundRawExpression raw: return ExpandTrusted(raw.CSharp, frame, frame + ".TargetObject!");
            default: _context.Error("The backend does not recognize expression '" + value.GetType().Name + "'.", value.Span); return "default!";
        }
    }

    private static string ArrayCreation(BoundArrayExpression array)
    {
        var element = array.ArrayType.ElementType;
        var suffix = string.Empty;
        while (element is IArrayTypeSymbol nested)
        { suffix += "[" + new string(',', nested.Rank - 1) + "]"; element = nested.ElementType; }
        return "new " + element.CSharpName() + "[" + array.Values.Length + "]" + suffix;
    }

    private string Convert(BoundExpression expression, INamedTypeSymbol converter, ITypeSymbol resultType, string frame, Func<string> value)
    {
        var local = _context.Temporary("converter");
        _context.Writer.Line("var " + local + " = new " + converter.CSharpName() + "();");
        if (!expression.SuppressSourceInfo && _context.Document.Runtime.SourceInfo != null)
            new SourceInfoEmitter(_context).EmitConstructed(local, expression.SourceInfoSpan ?? BoundSourceInfo.ValueLocation(_context.Document.Syntax, expression.Span));
        return "((" + resultType.CSharpName() + ")" + local + ".ConvertFrom(" + frame + ", " + CSharpNames.InvariantCulture + ", " + value() + ")!)";
    }

    public string[] EmitArguments(IMethodSymbol method, System.Collections.Immutable.ImmutableArray<BoundExpression> arguments, string frame)
    {
        var values = new string[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            var value = Emit(arguments[index], frame);
            var local = _context.Temporary("argument");
            _context.Writer.Line(method.Parameters[index].Type.CSharpName() + " " + local + " = " + value + ";");
            values[index] = local;
        }
        return values;
    }

    public string EmitInitialized(BoundExpression value, string frame,
        System.Collections.Immutable.ImmutableArray<BoundArgumentInitialization> initializers, IReadOnlyList<string> arguments)
    {
        if (initializers.IsDefaultOrEmpty) return Emit(value, frame);
        if (value is BoundObjectExpression child)
            return _objects.Emit(child.Object, frame, null, null,
                target => ArgumentInitializerEmitter.Emit(_context, target, initializers, arguments));
        _context.Error("An argument initializer requires a construction expression.", value.Span);
        return Emit(value, frame);
    }

    public string ExpandTrusted(string source, string frame, string target) => source.Replace("$context", frame).Replace("$target", target).Replace("$root", _context.RootVariable);

    private string Deferred(BoundDeferredExpression deferred, string parentFrame)
    {
        var writer = _context.Writer;
        var name = _context.Temporary("factory"); var frame = _context.Temporary("deferredContext");
        var incoming = _context.Temporary("incoming"); var root = _context.Temporary("ownerRoot");
        var returnType = deferred.FactoryReturnType?.CSharpName() ?? "object";
        writer.Open((deferred.UsesFunctionPointer ? "static " : string.Empty) + returnType + " " + name + "(" + CSharpNames.Provider + "? " + incoming + ")");
        _objects.EmitDeferredContext(frame, parentFrame, incoming, deferred.UsesFunctionPointer);
        writer.Open("try");
        writer.Line("var " + root + " = (" + _context.Document.Root!.Type.CSharpName() + ")" + frame + ".RootObject!;");
        var saved = _context.RootVariable; _context.RootVariable = root;
        try
        {
            var content = Emit(deferred.Content, frame); var result = _context.Temporary("template");
            writer.Line(returnType + " " + result + " = " + content + ";");
            _objects.Complete(frame, result); writer.Line("return " + result + ";");
        }
        finally { _context.RootVariable = saved; }
        writer.Close();
        ConstructionFailureEmitter.Emit(_context, frame);
        writer.Close();
        if (deferred.Customizer == null) return "(" + deferred.TargetType.CSharpName() + ")" + name;
        var factory = deferred.UsesFunctionPointer
            ? "(global::System.IntPtr)(delegate* managed<" + CSharpNames.Provider + ", " + returnType + ">)&" + name
            : "(" + deferred.Customizer.Parameters[0].Type.CSharpName() + ")" + name;
        var call = deferred.Customizer.ContainingType.CSharpName() + "." + CSharpNames.Method(deferred.Customizer) + "(" + factory + ", " + parentFrame + ")";
        if (!deferred.UsesFunctionPointer) return call;
        var value = _context.Temporary("deferred"); writer.Line(deferred.TargetType.CSharpName() + " " + value + ";");
        writer.Open("unsafe"); writer.Line(value + " = " + call + ";"); writer.Close(); return value;
    }
}
