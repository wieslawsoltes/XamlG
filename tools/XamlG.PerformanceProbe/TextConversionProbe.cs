using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class TextConversionProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        // Bind the pre-existing private entry points once, outside timing. Both
        // compiler revisions execute the same delegate, setup and call sites.
        var parse = typeof(ValueBinder).GetMethod("FindParse", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ValueBinder, ITypeSymbol, IMethodSymbol?>>();
        var converter = typeof(ValueBinder).GetMethod("FindConverter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<ValueBinder, ISymbol?, INamedTypeSymbol?>>();
        var enums = typeof(ValueBinder).GetMethod("TryEnum", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<string, INamedTypeSymbol, TextSpan, BoundExpression?>>();
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        const string model = """
            using System;
            using System.Collections.Generic;
            using System.ComponentModel;
            using System.Globalization;
            using XamlG.Runtime;
            namespace ConversionProbe {
                public class Root { [Content] public List<Item> Children { get; } = new(); }
                public class Item {
                    public Parsed P { get; set; }
                    public Token C { get; set; }
                    public Flags F { get; set; }
                    public int N { get; set; }
                    [TypeConverter(typeof(TokenConverter))] public Token Override { get; set; }
                }
                public class Parsed {
                    public string Text { get; set; }
                    public static Parsed Parse(string text) => new() { Text = "one:" + text };
                    public static Parsed Parse(string text, IFormatProvider provider) => new() { Text = "two:" + text };
                    public static Parsed Parse(string text, CultureInfo culture) => new() { Text = "culture:" + text };
                }
                [TypeConverter(typeof(TokenConverter))] public class Token { public string Text { get; set; } }
                [TypeConverter(" ConversionProbe.TokenConverter , ignored, Version=1.0.0.0 ")] public class NamedToken { }
                public class TokenConverter : TypeConverter {
                    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
                        => new Token { Text = (string)value };
                }
                public class Empty { }
                [Obsolete] public class Unrelated { }
                [System.Flags] public enum Flags { A = 1, B = 2, C = 4, D = 8 }
                public enum Plain { A = 1, B = 2 }
            }
            """;
        var uniqueSource = "namespace ConversionProbe { " + string.Concat(Enumerable.Range(0, 256).Select(i =>
            "[System.ComponentModel.TypeConverter(typeof(TokenConverter))] public class Unique" + i +
            " { public static Unique" + i + " Parse(string text) => new(); }")) + " }";
        var compilation = CSharpCompilation.Create("ConversionProbe", new[] { CSharpSyntaxTree.ParseText(model + uniqueSource, options, "Model.cs") },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        Require(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Invalid conversion fixture model");
        var syntax = XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:ConversionProbe'/>", "Probe.xaml");
        BindingContext Context(RoslynTypeSystem owner) => BindingContext.CreateSignatureProbe(syntax, owner, XamlFrameworkProfile.Portable, new());
        var types = new RoslynTypeSystem(compilation);
        var context = Context(types);
        var parsed = types.Find("ConversionProbe.Parsed")!;
        var empty = types.Find("ConversionProbe.Empty")!;
        var token = types.Find("ConversionProbe.Token")!;
        var named = types.Find("ConversionProbe.NamedToken")!;
        var unrelated = types.Find("ConversionProbe.Unrelated")!;
        var member = types.Find("ConversionProbe.Item")!.GetMembers("Override").Single();
        var unique = Enumerable.Range(0, 256).Select(i => types.Find("ConversionProbe.Unique" + i)!).ToArray();
        var flags = types.Find("ConversionProbe.Flags")!;
        var plain = types.Find("ConversionProbe.Plain")!;
        var signature = new StringBuilder();
        foreach (var target in unique.Cast<ITypeSymbol>().Concat(new[] { parsed, empty, token, named, unrelated }))
        {
            signature.Append(parse(context.Values, target)?.ToDisplayString() ?? "<none>").Append('\n');
            signature.Append(converter(context.Values, target)?.ToDisplayString() ?? "<none>").Append('\n');
        }
        measure("conversion-parse-warm-hit", 10000, () => parse(context.Values, parsed)!.Parameters.Length);
        measure("conversion-parse-warm-miss", 10000, () => parse(context.Values, empty) == null ? 1 : 0);
        measure("conversion-parse-cold-single", 20, () => parse(Context(new(compilation)).Values, parsed)!.Parameters.Length);
        measure("conversion-parse-cold-repeated-1024", 3, () => {
            var current = Context(new(compilation)); var sum = 0;
            for (var i = 0; i < 1024; i++) sum += parse(current.Values, parsed)!.Parameters.Length;
            return sum;
        });
        measure("conversion-parse-cold-unique-256", 3, () => {
            var current = Context(new(compilation)); var sum = 0;
            foreach (var target in unique) sum += parse(current.Values, target)!.Parameters.Length;
            return sum;
        });
        foreach (var (name, symbol) in new (string, ISymbol)[] {
            ("type", token), ("name", named), ("member", member), ("unrelated", unrelated), ("empty", empty) })
            measure("conversion-converter-warm-" + name, 10000, () => converter(context.Values, symbol)?.Name.Length ?? 0);
        measure("conversion-converter-cold-single", 20, () => converter(Context(new(compilation)).Values, named)!.Name.Length);
        measure("conversion-converter-cold-repeated-1024", 3, () => {
            var current = Context(new(compilation)); var sum = 0;
            for (var i = 0; i < 1024; i++) sum += converter(current.Values, named)!.Name.Length;
            return sum;
        });
        measure("conversion-converter-cold-unique-256", 3, () => {
            var current = Context(new(compilation)); var sum = 0;
            foreach (var target in unique) sum += converter(current.Values, target)!.Name.Length;
            return sum;
        });
        foreach (var (name, text, type) in new[] {
            ("plain", "A", plain), ("flags-4", " A , B , C , D ", flags),
            ("flags-64", string.Join(",", Enumerable.Repeat(" A ", 64)), flags), ("invalid", "A,,B", flags) })
        {
            var span = new TextSpan(3, text.Length);
            var value = enums(text, type, span);
            signature.Append(value is BoundEnumExpression e ? string.Join("|", e.Fields.Select(f => f.ToDisplayString())) : "<none>");
            measure("conversion-enum-" + name, 1000, () => (enums(text, type, span) as BoundEnumExpression)?.Fields.Length ?? 0);
        }
        foreach (var (name, type) in new[] { ("parsed", parsed), ("converted", token) })
        {
            measure("conversion-text-" + name + "-1024", 3, () => {
                var sum = 0;
                for (var i = 0; i < 1024; i++) sum += context.Values.TryText("value", type, NamespaceScope.Empty, new(i, 5))!.Span.Start;
                return sum;
            });
            var value = context.Values.TryText("source-value", type, NamespaceScope.Empty, new(7, 12));
            signature.Append(value?.GetType().Name).Append(value?.Span).Append(value?.Type?.ToDisplayString());
        }
        foreach (var count in new[] { 32, 1024 })
        {
            var xaml = "<Root xmlns='clr-namespace:ConversionProbe'>" +
                string.Concat(Enumerable.Range(0, count).Select(i => "<Item P='parse" + i + "' C='convert" + i + "' F='A, B' N='12'/>")) + "</Root>";
            var tree = XamlSyntaxTree.Parse(xaml, "Conversion" + count + ".xaml");
            var bound = new XamlCompiler().Bind(tree, types);
            Require(bound.Success, string.Join("\n", bound.Diagnostics));
            var emission = new CSharpEmitter().Emit(bound);
            Require(emission.Success, string.Join("\n", emission.Diagnostics));
            using var stream = new MemoryStream();
            var output = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(emission.Source, options, emission.HintName)).Emit(stream);
            Require(output.Success, "Generated conversion fixture failed: " + string.Join("\n", output.Diagnostics));
            signature.Append(emission.Source).Append(JsonSerializer.Serialize(emission.SourceMappings)).Append(JsonSerializer.Serialize(emission.Diagnostics));
            measure("conversion-bind-warm-" + count, count == 32 ? 3 : 1,
                () => new XamlCompiler().Bind(tree, types).Root!.Assignments.Length);
            measure("conversion-bind-cold-" + count, count == 32 ? 3 : 1,
                () => new XamlCompiler().Bind(tree, new RoslynTypeSystem(compilation)).Root!.Assignments.Length);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
