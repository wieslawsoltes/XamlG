using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;

namespace XamlG.CSharp;

/// <summary>Shares typed construction and registration without moving assignment or consumption.</summary>
internal sealed class ConstructionFactoryEmitter(EmissionContext context)
{
    private readonly Dictionary<(string Type, bool Initialize), (string Name, SharedGeneratedSource Source)> _factories = new();
    public IEnumerable<SharedGeneratedSource> Sources => _factories.Values.Select(factory => factory.Source);
    private string Namespace => context.Document.Options.GeneratedNamespace + ".Construction";
    private string Alias => "__c_" + context.Id;

    public bool TryEmit(BoundObject value, string parent, SourceInfoEmitter source,
        out string target, out string frame, out bool initialized)
    {
        target = frame = string.Empty;
        initialized = false;
        if (!context.ShareCachedValues || value.IsRoot || context.Document.Runtime.SourceInfo != null ||
            value.FactoryMethod != null || !value.Arguments.IsDefaultOrEmpty ||
            value.Constructor is not { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 0 } ||
            value.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsGenericType: false } type) return false;
        for (var owner = type; owner != null; owner = owner.ContainingType)
            if (owner.DeclaredAccessibility != Accessibility.Public) return false;

        // Names and generated named fields must be installed before BeginInit.
        // Named fields can also originate in a registered CLR name assignment.
        initialized = value.SupportsInitialize && value.Name == null &&
            !value.Assignments.Any(assignment => assignment is BoundSetAssignment { RegisterName: true });
        var typeName = type.CSharpName();
        var key = (typeName, initialized);
        if (!_factories.TryGetValue(key, out var factory))
        {
            var body = new CSharpWriter { Indent = 2 };
            body.Open("internal static " + CSharpNames.Context + " Create(" + CSharpNames.Context +
                " __parent, string __key, global::XamlG.Runtime.XamlSourceInfoTable __sources, int __sourceIndex, out " + typeName + " __value)");
            body.Line("__value = new " + typeName + "();");
            body.Line("var __frame = __parent.PushConstructed(__value, __key, __sources[__sourceIndex]);");
            if (initialized) body.Line("((global::System.ComponentModel.ISupportInitialize)__value).BeginInit();");
            body.Line("return __frame;");
            body.Close();
            var bodyText = body.ToString();
            var name = "C_" + context.StableId(bodyText);
            var writer = new CSharpWriter();
            writer.Line("#nullable enable annotations"); writer.Line("#nullable disable warnings");
            writer.Open("namespace " + Namespace); writer.Open("internal static class " + name);
            writer.Append(bodyText); writer.Close(); writer.Close();
            factory = (name, new("global::" + Namespace + "." + name, writer.ToString()));
            _factories.Add(key, factory);
        }
        var receiver = context.UsePropertyAliases ? Alias + "." + factory.Name : factory.Source.TypeName;
        var argument = context.Locals.OutArgument(typeName, "object", out target);
        var node = context.ConstructionParameters?.Key(value) ?? CSharpNames.Literal(value.Key);
        var index = context.ConstructionParameters?.SourceIndex(value) ?? source.Index(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        frame = context.Locals.Declare(CSharpNames.Context, receiver + ".Create(" + parent + ", " + node + ", " +
            context.SourceInfoTable + ", " + index + ", " + argument + ")", "context", inferred: true);
        return true;
    }

    public void InsertAlias(int position, int indent)
    {
        if (_factories.Count == 0 || !context.UsePropertyAliases) return;
        var alias = new string(' ', indent * 4) + "using " + Alias + " = global::" + Namespace + ";\n";
        context.Writer.Insert(position, alias);
        for (var i = 0; i < context.Mappings.Count; i++)
        {
            var mapping = context.Mappings[i];
            if (mapping.GeneratedSpan.Start >= position)
                context.Mappings[i] = mapping with { GeneratedSpan = new(mapping.GeneratedSpan.Start + alias.Length, mapping.GeneratedSpan.Length) };
        }
    }
}
