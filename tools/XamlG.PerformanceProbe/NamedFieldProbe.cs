using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class NamedFieldProbe
{
    public static List<string> Run(Action<string, int, Func<int>> measure)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace ProbeModel {
                public class Panel { [Content] public List<Item> Children { get; } = new(); }
                public class Item { public string Text { get; set; } = ""; }
                public partial class View : Panel { }
            }
            """;
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal);
        var references = paths.Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("NamedFieldProbe", new[] { CSharpSyntaxTree.ParseText(model) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var sources = new List<string>();
        foreach (var count in new[] { 100, 1000, 5000 })
        {
            var xaml = "<Panel xmlns='clr-namespace:ProbeModel' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='ProbeModel.View'>" +
                string.Concat(Enumerable.Range(0, count).Select(i => "<Item x:Name='item" + i + "' Text='value'/>")) + "</Panel>";
            // Binding and model setup are intentionally outside emission timings.
            var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Named.xaml"), compilation);
            if (!bound.Success) throw new InvalidOperationException(string.Join("\n", bound.Diagnostics));
            var output = new CSharpEmitter().Emit(bound);
            if (!output.Success) throw new InvalidOperationException(string.Join("\n", output.Diagnostics));
            sources.Add(output.Source);
            sources.Add(System.Text.Json.JsonSerializer.Serialize(output.SourceMappings));
            measure("emit-named-fields-" + count, count == 100 ? 4 : 1, () => new CSharpEmitter().Emit(bound).Source.Length);
        }
        return sources;
    }
}
