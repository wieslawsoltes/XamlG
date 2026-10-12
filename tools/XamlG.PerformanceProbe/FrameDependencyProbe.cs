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

/// <summary>The same existing static entry point is bound once in both revisions.
/// No reflection, graph construction, code compilation or signatures are timed.</summary>
internal static class FrameDependencyProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var method = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.ValueEmitter", true)!
            .GetMethod("UsesFrame", BindingFlags.Static | BindingFlags.NonPublic, [typeof(BoundExpression)])
            ?? throw new MissingMethodException("ValueEmitter.UsesFrame");
        var uses = (Func<BoundExpression, bool>)method.CreateDelegate(typeof(Func<BoundExpression, bool>));
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace FrameProbe {
                public class Root { [Content] public List<View> Children {get;} = new(); }
                public class View { public object Value {get;set;} }
                public class Item { }
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("FrameProbe", [CSharpSyntaxTree.ParseText(model, options)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var arrayType = compilation.CreateArrayTypeSymbol(objectType);
        var literal = new BoundConstantExpression(null, objectType, default);
        var service = new BoundServiceExpression(objectType, default);
        var signature = new StringBuilder();
        Check("frame-leaf-literal", literal, false, 10000);
        Check("frame-leaf-service", service, true, 10000);
        foreach (var count in new[] { 4, 32, 1024 })
        foreach (var required in new[] { false, true })
        {
            var values = Enumerable.Repeat<BoundExpression>(literal, count).ToImmutableArray();
            if (required) values = values.SetItem(count - 1, service);
            Check("frame-wide-" + count + (required ? "-late" : "-literal"), new BoundArrayExpression(values, arrayType, default), required, count == 1024 ? 100 : 1000);
        }
        foreach (var count in new[] { 32, 4096 })
        foreach (var required in new[] { false, true })
        {
            BoundExpression value = required ? service : literal;
            for (var i = 0; i < count; i++) value = new BoundCastExpression(value, objectType, default);
            Check("frame-unary-" + count + (required ? "-service" : "-literal"), value, required, count == 32 ? 1000 : 10);
        }
        foreach (var depth in new[] { 10, 16 })
        foreach (var required in new[] { false, true })
        {
            BoundExpression value = literal;
            for (var i = 0; i < depth; i++) value = new BoundArrayExpression([value, value], arrayType, default);
            if (required) value = new BoundArrayExpression([value, service], arrayType, default);
            Check("frame-shared-" + depth + (required ? "-late" : "-literal"), value, required, depth == 10 ? 10 : 2);
        }
        foreach (var depth in new[] { 6, 10 })
        foreach (var required in new[] { false, true })
        {
            BoundExpression Tree(int n) => n == 0 ? literal : new BoundArrayExpression([Tree(n - 1), Tree(n - 1)], arrayType, default);
            var value = Tree(depth);
            if (required) value = new BoundArrayExpression([value, service], arrayType, default);
            Check("frame-unshared-" + depth + (required ? "-late" : "-literal"), value, required, 10);
        }
        BoundExpression suffix = literal;
        for (var i = 0; i < 1024; i++) suffix = new BoundCastExpression(suffix, objectType, default);
        Check("frame-shared-unary-suffix-1024", new BoundArrayExpression(
            Enumerable.Repeat(suffix, 1024).ToImmutableArray(), arrayType, default), false, 2);

        foreach (var count in new[] { 16, 128 })
        foreach (var width in new[] { 4, 32 })
        {
            var xaml = "<Root xmlns='clr-namespace:FrameProbe'>" + string.Concat(Enumerable.Repeat("<View Value='{Values}'/>", count)) + "</Root>";
            var profile = XamlFrameworkProfile.Portable with { MarkupBindingRules = [new ArrayRule(width)] };
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Frame.xaml"), compilation, profile);
            Require(bound.Success, "Frame fixture failed to bind: " + string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Frame fixture failed to emit");
            using (var stream = new MemoryStream())
            {
                var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, options)).Emit(stream);
                Require(result.Success, "Generated frame fixture failed to compile: " + string.Join("\n", result.Diagnostics));
            }
            var name = "emit-frame-arrays-" + count + "x" + width;
            measure(name, count == 16 ? 3 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source).Append('\n')
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append('\n')
                .Append(JsonSerializer.Serialize(output.Diagnostics)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));

        void Check(string name, BoundExpression expression, bool expected, int operations)
        {
            Require(uses(expression) == expected, "Incorrect frame predicate: " + name);
            measure(name, operations, () => uses(expression) ? 1 : 0);
            signature.Append(name).Append('=').Append(expected ? '1' : '0').Append('\n');
        }
    }

    private sealed class ArrayRule(int count) : IXamlMarkupBindingRule
    {
        public bool TryBind(BindingContext context, MarkupExtensionSyntax syntax, ITypeSymbol targetType,
            NamespaceScope scope, out BoundExpression? expression)
        {
            expression = null;
            if (syntax.Name != "Values") return false;
            var item = context.Types.Find("FrameProbe.Item")!;
            var creation = new BoundNewExpression(item.InstanceConstructors.Single(c => c.Parameters.IsEmpty), [], syntax.Span);
            expression = new BoundArrayExpression(Enumerable.Repeat<BoundExpression>(creation, count).ToImmutableArray(),
                context.Types.Compilation.CreateArrayTypeSymbol(context.Types.Special(SpecialType.System_Object)), syntax.Span);
            return true;
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
