using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class EmissionBookkeepingProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace BookkeepingProbe {
                public class Root { [Content] public List<Item> Children { get; } = new(); }
                public partial class Page : Root { }
                public class Item {
                    public string P0 { get; set; } = ""; public string P1 { get; set; } = "";
                    public string P2 { get; set; } = ""; public string P3 { get; set; } = "";
                    public string P4 { get; set; } = ""; public string P5 { get; set; } = "";
                    public string P6 { get; set; } = ""; public string P7 { get; set; } = "";
                }
            }
            """;
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var compilation = CSharpCompilation.Create("EmissionBookkeepingProbe", [CSharpSyntaxTree.ParseText(model, options)],
            paths.Select(path => MetadataReference.CreateFromFile(path)), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Root xmlns='clr-namespace:BookkeepingProbe'/>", "Page.xaml"), compilation);
        Require(document.Success, "Bookkeeping setup failed");
        var signature = new StringBuilder();

        // Bind reflection once, outside every timed region. Both revisions expose
        // precisely these existing members; no candidate-only benchmark shortcut.
        var contextType = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.EmissionContext", true)!;
        var doc = Expression.Parameter(typeof(BoundDocument), "document");
        var create = Expression.Lambda<Func<BoundDocument, IDisposable>>(
            Expression.Convert(Expression.New(contextType.GetConstructor([typeof(BoundDocument), typeof(CancellationToken)])!,
                doc, Expression.Constant(CancellationToken.None)), typeof(IDisposable)), doc).Compile();
        var instance = Expression.Parameter(typeof(IDisposable), "context");
        Func<IDisposable, string> Getter(string name, bool method = false)
        {
            var receiver = Expression.Convert(instance, contextType);
            Expression body = method ? Expression.Call(receiver, contextType.GetMethod(name)!) : Expression.Property(receiver, name);
            return Expression.Lambda<Func<IDisposable, string>>(body, instance).Compile();
        }
        var services = Getter("ServicesType");
        var table = Getter("SourceInfoTable");
        var setter = Getter("SourceInfoSetter", true);
        using (var context = create(document))
        {
            foreach (var entry in new[] { (Name: "services", Read: services), (Name: "table", Read: table), (Name: "setter", Read: setter) })
            {
                var name = "emission-names-warm-" + entry.Name;
                measure(name, 10000, () => entry.Read(context).Length);
                signature.Append(name).Append(':').Append(entry.Read(context)).Append('\n');
            }
        }
        foreach (var count in new[] { 0, 1, 1000 })
        {
            var name = "emission-names-context-reads" + count;
            measure(name, 32, () =>
            {
                // Includes context/SHA construction, first-use string retention
                // and disposal. Zero-use measures the additional field cost.
                using var context = create(document);
                var sum = 0;
                for (var i = 0; i < count; i++) sum += services(context).Length + table(context).Length + setter(context).Length;
                return sum;
            });
            using var snapshot = create(document);
            signature.Append(name).Append(':').Append(services(snapshot)).Append(':').Append(table(snapshot)).Append(':').Append(setter(snapshot)).Append('\n');
        }

        foreach (var count in new[] { 1, 512 })
        foreach (var named in new[] { false, true })
        foreach (var escaped in new[] { false, true })
        foreach (var lines in new[] { false, true })
        {
            var xaml = "<Root xmlns='clr-namespace:BookkeepingProbe' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
                (named ? " x:Class='BookkeepingProbe.Page'" : "") + ">\n" +
                string.Concat(Enumerable.Range(0, count).Select(i => "<Item" + (named ? " x:Name='item" + i + "'" : "") +
                    " P0='a' P1='b' P2='c' P3='d' P4='e' P5='f' P6='g' P7='h'/>\n")) + "</Root>";
            // Backslashes exercise literal escaping without an embedded quote,
            // which is not representable by the historical #line filename grammar.
            var path = escaped ? "generated/" + string.Concat(Enumerable.Repeat("folder\\escaped_part/", 16)) + "Page.xaml" : "Page.xaml";
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, path), compilation,
                XamlFrameworkProfile.Portable, new XamlCompilerOptions { EmitLineDirectives = lines });
            Require(bound.Success, "Bookkeeping fixture binding failed: " + string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            Require(output.Success, "Bookkeeping fixture emission failed: " + string.Join("\n", output.Diagnostics));
            using (var stream = new MemoryStream())
            {
                var compiled = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, options)).Emit(stream);
                Require(compiled.Success, "Bookkeeping fixture compilation failed: " + string.Join("\n", compiled.Diagnostics));
            }
            var name = "bookkeeping-emission-" + count + "-" + (named ? "named" : "anonymous") + "-" +
                (escaped ? "escaped" : "short") + "-lines" + (lines ? "on" : "off");
            measure(name, count == 1 ? 16 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
            signature.Append(name).Append('\n').Append(output.Source).Append('\n')
                .Append(JsonSerializer.Serialize(output.SourceMappings)).Append('\n')
                .Append(JsonSerializer.Serialize(output.Diagnostics)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
