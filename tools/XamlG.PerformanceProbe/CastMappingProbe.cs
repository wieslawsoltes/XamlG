using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class CastMappingProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace CastMapProbe {
                public class Root { [Content] public List<View> Children { get; } = new(); }
                public class View {
                    public object Value { get; set; }
                    public string P1 { get; set; } public string P2 { get; set; } public string P3 { get; set; }
                    public string P4 { get; set; } public string P5 { get; set; } public string P6 { get; set; } public string P7 { get; set; }
                }
            }
            """;
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var compilation = CSharpCompilation.Create("CastMappingProbe", [CSharpSyntaxTree.ParseText(model, options)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:CastMapProbe'/>", "Cast.xaml"), compilation);
        Require(document.Success, "Setup binding failed");
        var assembly = typeof(CSharpEmitter).Assembly;
        var context = Activator.CreateInstance(assembly.GetType("XamlG.CSharp.EmissionContext", true)!, document, CancellationToken.None)!;
        using var ownedContext = (IDisposable)context;
        var objects = Activator.CreateInstance(assembly.GetType("XamlG.CSharp.ObjectEmitter", true)!, context)!;
        var emitterType = assembly.GetType("XamlG.CSharp.ValueEmitter", true)!;
        var emitter = Activator.CreateInstance(emitterType, context, objects)!;
        var emit = (Func<BoundExpression, string, string>)emitterType.GetMethod("Emit")!
            .CreateDelegate(typeof(Func<BoundExpression, string, string>), emitter);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var signature = new StringBuilder();
        foreach (var depth in new[] { 0, 1, 2, 3, 32, 256, 1024 })
        foreach (var mixed in new[] { false, true })
        {
            BoundExpression expression = new BoundConstantExpression("value", stringType, default);
            for (var i = 0; i < depth; i++) expression = new BoundCastExpression(expression,
                mixed && i % 2 == 1 ? stringType : objectType, new(i, 1));
            var name = "cast-chain-" + (mixed ? "mixed-" : "object-") + depth;
            measure(name, depth >= 256 ? 5 : 100, () => emit(expression, "__frame").Length);
            signature.Append(name).Append(':').Append(emit(expression, "__frame")).Append('\n');
        }
        foreach (var count in new[] { 32, 512 })
        foreach (var depth in new[] { 0, 8 })
        foreach (var lines in new[] { false, true })
        {
            var xaml = "<Root xmlns='clr-namespace:CastMapProbe'>\n" + string.Concat(Enumerable.Repeat(
                "<View Value='value' P1='a' P2='b' P3='c' P4='d' P5='e' P6='f' P7='g'/>\n", count)) + "</Root>";
            var profile = XamlFrameworkProfile.Portable with { BindingRules = [new CastRule(depth)] };
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Mapped.xaml"), compilation, profile,
                new XamlCompilerOptions { EmitLineDirectives = lines });
            Require(bound.Success, "Mapped fixture failed to bind");
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Mapped fixture failed to emit");
            using (var stream = new MemoryStream())
            {
                var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, options)).Emit(stream);
                Require(result.Success, "Generated fixture failed to compile: " + string.Join("\n", result.Diagnostics));
            }
            var name = "mapped-emission-" + count + "-casts" + depth + "-lines" + (lines ? "on" : "off");
            measure(name, count == 32 ? 4 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source).Append('\n')
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append('\n')
                .Append(JsonSerializer.Serialize(output.Diagnostics)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private sealed class CastRule(int depth) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            BoundExpression value = new BoundConstantExpression(attribute.Value, context.Types.Special(SpecialType.System_String), attribute.ValueSpan);
            for (var i = 0; i < depth; i++) value = new BoundCastExpression(value,
                context.Types.Special(i % 2 == 0 ? SpecialType.System_Object : SpecialType.System_String), attribute.ValueSpan);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.NameSpan), value, attribute.Span));
            return true;
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
