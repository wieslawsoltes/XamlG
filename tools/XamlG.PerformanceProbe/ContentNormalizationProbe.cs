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

internal static class ContentNormalizationProbe
{
    private delegate XamlSyntaxNode[] NormalizeCall(IEnumerable<XamlSyntaxNode> source, ITypeSymbol target, bool collection, NamespaceScope scope, bool direct);
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var signature = new StringBuilder();
        foreach (var size in new[] { 32, 4096 })
        {
            var samples = new Dictionary<string, string> {
                ["canonical"] = new string('x', size),
                ["spaces"] = string.Join(" ", Enumerable.Repeat("ab", size / 3)),
                ["changed"] = string.Concat(Enumerable.Repeat(" a\t \nb \r\n", Math.Max(1, size / 9))),
                ["blank"] = new string('\t', size),
                ["late-change"] = new string('x', size - 3) + "\t y",
                ["sparse-change"] = new string('x', size / 2) + "\t" + new string('y', size / 2),
                ["boundary-change"] = "\t" + new string('x', size) + "\r\n"
            };
            foreach (var item in samples)
            {
                var text = item.Value;
                measure("whitespace-collapse-" + item.Key + "-" + size, 1000, () => XmlWhitespace.Collapse(text, true, true).Length);
                foreach (var left in new[] { false, true }) foreach (var right in new[] { false, true })
                    signature.Append(JsonSerializer.Serialize(XmlWhitespace.Collapse(text, left, right)));
            }
        }
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace ContentProbe {
                [WhitespaceSignificantCollection] public class Words : List<object> { }
                [TrimSurroundingWhitespace] public class Trim { }
                public class Plain { }
                public class Root { [Content] public Words Children { get; } = new(); }
                public class TextRoot { [Content] public string Text { get; set; } = ""; }
                public class Panel { [Content] public List<TextRoot> Children { get; } = new(); }
            }
            """;
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ContentNormalizationProbe", new[] { CSharpSyntaxTree.ParseText(model, parse) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Require(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Invalid normalization model");
        var types = new RoslynTypeSystem(compilation);
        var normalizerType = typeof(XamlCompiler).Assembly.GetType("XamlG.Compiler.ContentWhitespaceNormalizer", true)!;
        const string ns = " xmlns='clr-namespace:ContentProbe' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
        foreach (var count in new[] { 1, 2, 32, 256, 2048 })
        foreach (var preserve in new[] { false, true })
        {
            var tree = XamlSyntaxTree.Parse("<TextRoot" + ns + (preserve ? " xml:space='preserve'" : "") + ">" +
                string.Concat(Enumerable.Repeat("word <!--gap-->", count)) + "</TextRoot>", "Fragments.xaml");
            var normalize = Prepare(tree, types.Special(SpecialType.System_String), collection: false);
            var mode = preserve ? "preserve" : "collapse";
            measure("content-fragments-" + mode + "-" + count, count < 256 ? 20 : 1, normalize);
            CompileAndSign(tree);
            if (count is 32 or 2048)
                measure("content-bind-fragments-" + mode + "-" + count, count == 32 ? 5 : 1,
                    () => new XamlCompiler().Bind(tree, types, XamlFrameworkProfile.Portable).Symbols.Length);
        }
        foreach (var attributes in new[] { 8, 128, 512 })
        {
            var names = string.Join(" ", Enumerable.Range(0, attributes).Select(i => "xml:p" + i + "='v'"));
            var tree = XamlSyntaxTree.Parse("<Root" + ns + "><x:String " + names + "> a  b </x:String></Root>", "Literal.xaml");
            var normalize = Prepare(tree, types.Find("ContentProbe.Words")!, collection: true);
            measure("content-literal-attributes-" + attributes, attributes == 8 ? 20 : 2, normalize);
            CompileAndSign(tree);
        }
        foreach (var count in new[] { 16, 1024 })
        {
            var tree = XamlSyntaxTree.Parse("<Root" + ns + ">" + string.Concat(Enumerable.Repeat(" <Plain/>", count)) + "</Root>", "Elements.xaml");
            measure("content-elements-" + count, count == 16 ? 10 : 1, Prepare(tree, types.Find("ContentProbe.Words")!, collection: true));
            CompileAndSign(tree);
        }
        foreach (var count in new[] { 32, 512 })
        {
            var tree = XamlSyntaxTree.Parse("<Panel" + ns + ">" + string.Concat(Enumerable.Repeat("<TextRoot>canonical text</TextRoot>", count)) + "</Panel>", "Canonical.xaml");
            CompileAndSign(tree);
            measure("content-bind-canonical-" + count, count == 32 ? 5 : 1, () => new XamlCompiler().Bind(tree, types, XamlFrameworkProfile.Portable).Symbols.Length);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));

        Func<int> Prepare(XamlSyntaxTree tree, ITypeSymbol target, bool collection)
        {
            var context = BindingContext.CreateSignatureProbe(tree, types, XamlFrameworkProfile.Portable, new());
            var normalize = (NormalizeCall)normalizerType.GetMethod("Normalize")!.CreateDelegate(typeof(NormalizeCall), Activator.CreateInstance(normalizerType, context)!);
            var scope = NamespaceScope.Empty.Push(tree.Root!);
            var result = normalize(tree.Root!.Children, target, collection, scope, false);
            foreach (var node in result) SignNode(node);
            signature.Append(JsonSerializer.Serialize(context.Diagnostics));
            signature.Append(JsonSerializer.Serialize(context.Symbols.Select(s => new { s.Span, s.Role, Symbol = s.Symbol.ToDisplayString() })));
            return () =>
            {
                context.Diagnostics.Clear(); context.Symbols.Clear();
                return normalize(tree.Root.Children, target, collection, scope, false).Length;
            };
        }
        void CompileAndSign(XamlSyntaxTree tree)
        {
            var bound = new XamlCompiler().Bind(tree, types, XamlFrameworkProfile.Portable);
            Require(bound.Success, "Normalization fixture binding failed: " + string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Normalization fixture emission failed");
            using var image = new MemoryStream();
            var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parse, output.HintName)).Emit(image);
            Require(result.Success, "Normalization fixture compilation failed: " + string.Join("\n", result.Diagnostics));
            signature.Append(output.Source).Append(JsonSerializer.Serialize(output.SourceMappings)).Append(JsonSerializer.Serialize(output.Diagnostics));
        }
        void SignNode(XamlSyntaxNode node)
        {
            signature.Append(JsonSerializer.Serialize(node, node.GetType()));
            if (node is XamlElementSyntax element) foreach (var child in element.Children) SignNode(child);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
