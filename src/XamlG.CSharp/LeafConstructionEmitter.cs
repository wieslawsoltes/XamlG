using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares typed construction of leaf objects without moving ProvideValue or argument evaluation.</summary>
internal sealed class LeafConstructionEmitter(EmissionContext context, ValueEmitter values, SourceInfoEmitter source)
{
    private readonly Dictionary<string, (string Name, BoundObject Object)> _factories = new(StringComparer.Ordinal);

    public bool CanShare(BoundObject value) => !value.IsRoot && value.Name == null && value.Type.IsReferenceType &&
        value.Assignments.IsEmpty && value.FactoryMethod == null && value.Constructor != null &&
        value.Arguments.Length == value.Constructor.Parameters.Length &&
        value.Constructor.Parameters.All(parameter => parameter.RefKind == RefKind.None) &&
        context.Document.Runtime.SourceInfo == null;

    public string Emit(BoundObject value, string frame)
    {
        var constructor = value.Constructor!;
        var key = value.Type.CSharpName() + "(" + string.Join(",", constructor.Parameters.Select(p => p.Type.CSharpName())) +
            "):" + value.SupportsInitialize;
        if (!_factories.TryGetValue(key, out var factory))
        {
            factory = ("__XamlGCreateLeaf_" + context.Id + "_" + _factories.Count, value);
            _factories.Add(key, factory);
        }
        var arguments = values.EmitArguments(constructor, value.Arguments, frame);
        var variable = context.Temporary("object");
        context.Writer.Line("var " + variable + " = " + factory.Name + "(" + frame + ", " + CSharpNames.Literal(value.Key) +
            ", " + source.Index(value) + (arguments.Length == 0 ? string.Empty : ", " + string.Join(", ", arguments)) + ");");
        return variable;
    }

    public void EmitHelpers()
    {
        foreach (var factory in _factories.Values)
        {
            var value = factory.Object;
            var parameters = value.Constructor!.Parameters;
            var declarations = parameters.Select((parameter, index) => parameter.Type.CSharpName() + " __argument" + index);
            var arguments = parameters.Select((_, index) => "__argument" + index);
            var writer = context.Writer;
            writer.Open("private static " + value.Type.CSharpName() + " " + factory.Name + "(" + CSharpNames.Context +
                " __frame, string __key, int __sourceIndex" + (parameters.Length == 0 ? string.Empty : ", " + string.Join(", ", declarations)) + ")");
            writer.Line("var __value = new " + value.Type.CSharpName() + "(" + string.Join(", ", arguments) + ");");
            writer.Line("__frame.PushConstructed(__value, __key, " + context.SourceInfoTable + "[__sourceIndex]);");
            if (value.SupportsInitialize)
            {
                writer.Line("((global::System.ComponentModel.ISupportInitialize)__value).BeginInit();");
                writer.Line("((global::System.ComponentModel.ISupportInitialize)__value).EndInit();");
            }
            writer.Line("return __value;");
            writer.Close();
        }
    }
}
