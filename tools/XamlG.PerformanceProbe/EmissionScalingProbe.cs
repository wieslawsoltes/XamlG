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

internal static class EmissionScalingProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        // Bind the same existing method in both revisions. Reflection and delegate
        // creation happen once, outside every measured batch.
        var literal = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.CSharpNames")!
            .GetMethod("Literal", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Func<string, string>>();
        using var snapshot = new MemoryStream();
        using var writer = new BinaryWriter(snapshot, Encoding.UTF8, leaveOpen: true);
        foreach (var (name, text, operations) in new[]
        {
            ("literal-ascii-short", "src/Views/CustomerDetails.axaml", 10000),
            ("literal-ascii-long", new string('a', 8192), 1000),
            ("literal-unicode-escaped", "\u03b1\u03b2\u03b3\r\n\"\\\ud800", 10000),
            ("literal-late-escape", new string('a', 8192) + "\n", 1000)
        })
        {
            measure(name, operations, () => literal(text).Length);
            writer.Write(literal(text));
        }
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        foreach (var width in new[] { 8, 64, 256 })
        {
            var properties = string.Concat(Enumerable.Range(0, width).Select(i => "public string P" + i + " { get; set; } = \"\";"));
            var model = "using System.Collections.Generic; using XamlG.Runtime; namespace WideProbe { " +
                "public class Root { [Content] public List<object> Children { get; } = new(); } " +
                "public class Holder { public Holder() { } public Holder(Leaf value) { Value = value; } public object Value { get; set; } } " +
                "public class Leaf { " + properties + " } public class WideExtension { " + properties +
                " public object ProvideValue() => this; } }";
            var compilation = CSharpCompilation.Create("WideProbe", new[] { CSharpSyntaxTree.ParseText(model) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var leaf = "<Holder><x:Arguments><Leaf " + string.Join(" ", Enumerable.Range(0, width).Select(i => "P" + i + "='value_" + i + "'")) + "/></x:Arguments></Holder>";
            var markup = "<Holder Value='{Wide " + string.Join(", ", Enumerable.Range(0, width).Select(i => "P" + i + "=value_" + i)) + "}'/>";
            foreach (var (kind, child) in new[] { ("leaf", leaf), ("markup", markup) })
            {
                var xaml = "<Root xmlns='clr-namespace:WideProbe' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + string.Concat(Enumerable.Repeat(child, 8)) + "</Root>";
                var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "src/Views/Wide.xaml"), compilation);
                if (!document.Success) throw new InvalidOperationException(string.Join("\n", document.Diagnostics));
                var emitted = new CSharpEmitter().Emit(document);
                if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
                var helper = kind == "leaf" ? "__XamlGCreateLeaf_" : "__XamlGAssignMarkup_";
                if (!emitted.Source.Contains(helper, StringComparison.Ordinal))
                    throw new InvalidOperationException("The " + kind + " fixture did not exercise its intended helper path.");
                measure("emit-wide-" + kind + "-" + width, width == 8 ? 8 : 2,
                    () => new CSharpEmitter().Emit(document).Source.Length);
                writer.Write(emitted.Source);
                writer.Write(JsonSerializer.Serialize(emitted.SourceMappings));
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(snapshot.ToArray()));
    }
}
