using System.Collections.Immutable;
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

/// <summary>Bind the same pre-existing entry points outside timing in each revision.
/// Reflection, inputs, compilation and signatures are never measured as emission.</summary>
internal static class ValueEmissionProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using System.ComponentModel;
            using System.Globalization;
            using XamlG.Runtime;
            namespace ValueEmitProbe {
                public class Root { [Content] public List<View> Children { get; } = new(); }
                public class View { public object Value { get; set; } }
                public class Box { public object Value; public Box(object value) { Value = value; } }
                public class Converter : TypeConverter {
                    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) => new Box(value);
                }
                public static class Input { public static object Read() => 17; }
            }
            """;
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var compilation = CSharpCompilation.Create("ValueEmissionProbe", [CSharpSyntaxTree.ParseText(model, options)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:ValueEmitProbe'/>", "Values.xaml"), compilation);
        Require(document.Success, "Setup binding failed");
        var assembly = typeof(CSharpEmitter).Assembly;
        var contextType = assembly.GetType("XamlG.CSharp.EmissionContext", true)!;
        var context = Activator.CreateInstance(contextType, document, CancellationToken.None)!;
        using var ownedContext = (IDisposable)context;
        var objects = Activator.CreateInstance(assembly.GetType("XamlG.CSharp.ObjectEmitter", true)!, context)!;
        var emitterType = assembly.GetType("XamlG.CSharp.ValueEmitter", true)!;
        var emitter = Activator.CreateInstance(emitterType, context, objects)!;
        var emit = (Func<BoundExpression, string, string>)emitterType.GetMethod("Emit")!
            .CreateDelegate(typeof(Func<BoundExpression, string, string>), emitter);
        var initialized = (Func<BoundExpression, string, ImmutableArray<BoundArgumentInitialization>, IReadOnlyList<string>, string>)
            emitterType.GetMethod("EmitInitialized")!.CreateDelegate(
                typeof(Func<BoundExpression, string, ImmutableArray<BoundArgumentInitialization>, IReadOnlyList<string>, string>), emitter);
        var arrayFormat = (Func<BoundArrayExpression, string>)emitterType.GetMethod("ArrayCreation", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<BoundArrayExpression, string>));
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var signature = new StringBuilder();
        foreach (var (name, value) in new (string, object?)[] { ("null", null), ("bool", true), ("string", "a\\b\n\"c\"") })
        {
            var expression = new BoundConstantExpression(value, objectType, default);
            var result = emit(expression, "__frame");
            Require(result == initialized(expression, "__frame", [], []), "Empty initializer output changed");
            measure("value-emit-" + name, 10000, () => emit(expression, "__frame").Length);
            measure("value-emit-empty-initializers-" + name, 10000, () => initialized(expression, "__frame", [], []).Length);
            signature.Append(name).Append(':').Append(result).Append('\n');
        }
        foreach (var depth in new[] { 0, 1, 2, 32, 256, 1024 })
        foreach (var mixed in new[] { false, true })
        {
            ITypeSymbol element = objectType;
            for (var i = 0; i < depth; i++) element = compilation.CreateArrayTypeSymbol(element, mixed ? i % 4 + 1 : 1);
            var value = new BoundArrayExpression([], compilation.CreateArrayTypeSymbol(element), default);
            var name = "value-array-shape-" + (mixed ? "mixed-" : "jagged-") + depth;
            measure(name, depth >= 256 ? 10 : 100, () => arrayFormat(value).Length);
            signature.Append(name).Append(':').Append(arrayFormat(value)).Append('\n');
        }
        foreach (var count in new[] { 32, 512 })
        foreach (var payload in new[] { false, true })
        {
            var xaml = "<Root xmlns='clr-namespace:ValueEmitProbe'>" +
                string.Concat(Enumerable.Repeat("<View Value='payload'/>", count)) + "</Root>";
            var profile = XamlFrameworkProfile.Portable with { BindingRules = [new ConversionRule(payload)] };
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Converters.xaml"), compilation, profile);
            Require(bound.Success, "Converter fixture failed to bind: " + string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Converter fixture failed to emit");
            using (var stream = new MemoryStream())
            {
                var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, options)).Emit(stream);
                Require(result.Success, "Generated converters failed to compile: " + string.Join("\n", result.Diagnostics));
            }
            var name = "value-emit-converters-" + (payload ? "value-" : "text-") + count;
            measure(name, count == 32 ? 5 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source).Append('\n')
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append('\n')
                .Append(JsonSerializer.Serialize(output.Diagnostics)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private sealed class ConversionRule(bool payload) : IXamlBindingRule
    {
        public bool TryBindAttribute(BindingContext context, ObjectBindingBuilder target, XamlAttributeSyntax attribute, NamespaceScope scope)
        {
            if (attribute.Name != "Value") return false;
            var type = context.Types.Special(SpecialType.System_Object);
            var converter = context.Types.Find("ValueEmitProbe.Converter")!;
            BoundExpression input = payload
                ? new BoundValueConverterExpression(new BoundCallExpression(
                    (IMethodSymbol)context.Types.Find("ValueEmitProbe.Input")!.GetMembers("Read").Single(), null, [], attribute.ValueSpan), converter, type, attribute.ValueSpan)
                : new BoundConverterExpression(attribute.Value, converter, type, attribute.ValueSpan);
            var value = new BoundValueConverterExpression(input, converter, type, attribute.ValueSpan);
            var property = (IPropertySymbol)target.Type.GetMembers("Value").Single();
            target.Assignments.Add(new BoundSetAssignment(new(property.Name, BoundMemberKind.Property, property, property.Type,
                property.GetMethod, property.SetMethod, attribute.NameSpan), value, attribute.Span));
            return true;
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
