using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using XamlG.Compiler;
using XamlG.CSharp.Integration;
using XamlG.CSharp.Resources;
using XamlG.Generator;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;

internal static class PipelineProbe
{
    private const string Model = """
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace Pipeline {
            public interface IRoot { object RootObject { get; } }
            public class Panel { [Content] public List<Item> Children { get; } = new(); }
            public class Item { public string Text { get; set; } = ""; public int Value {get;set;} }
            public static class Loader { public static void Load(object value) { } }
        }
        """;
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("PipelineProbe", new[] { CSharpSyntaxTree.ParseText(Model, parse, "Model.cs") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var profile = XamlFrameworkProfile.Portable with { Runtime = new() { Services = ImmutableArray.Create(
            new XamlServiceMapping("Pipeline.IRoot", XamlServiceKind.RootObject)) } };
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = 1 };
        static string Markup(string value, int count = 16) => "<Panel xmlns='clr-namespace:Pipeline'>" +
            string.Concat(Enumerable.Repeat("<Item Text='" + value + "' Value='12'/>", count)) + "</Panel>";
        var documents = Enumerable.Range(0, 64).Select(i => new XamlProjectDocument(
            XamlSyntaxTree.Parse(Markup("value" + i), "View" + i + ".xaml"), "View" + i + ".xaml")).ToArray();
        var edited = documents.ToArray();
        edited[31] = new(XamlSyntaxTree.Parse(Markup("edited"), documents[31].LogicalPath), documents[31].LogicalPath);
        var compiler = new XamlProjectCompiler();
        var cold = compiler.Compile(documents, compilation, profile, options);
        Require(cold.Success, "Cold project failed");
        var warm = compiler.Compile(documents, compilation, profile, options);
        Require(warm.Statistics == new XamlProjectStatistics(0, 64, 0, 64), "No-op project performed binding/emission");
        var edit = compiler.Compile(edited, compilation, profile, options);
        Require(edit.Success && edit.Statistics == new XamlProjectStatistics(1, 63, 1, 63), "Single-file edit invalidated unrelated output");
        Require(Signature(edit) == Signature(new XamlProjectCompiler().Compile(edited, compilation, profile, options)), "Warm/cold edit outputs differ");
        _ = compiler.Compile(documents, compilation, profile, options);
        measure("project-cold-64x16", 1, () => new XamlProjectCompiler().Compile(documents, compilation, profile, options).Documents.Length);
        measure("project-noop-64x16", 10, () => compiler.Compile(documents, compilation, profile, options).Documents.Length);
        var useEdit = false;
        measure("project-edit-one-of-64", 3, () => compiler.Compile((useEdit = !useEdit) ? edited : documents, compilation, profile, options).Statistics.BoundDocuments);
        var alternate = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class Changed { }", parse, "Changed.cs"));
        var useCompilation = false;
        measure("project-csharp-edit-64x16", 1, () => compiler.Compile(documents, (useCompilation = !useCompilation) ? alternate : compilation, profile, options).Statistics.BoundDocuments);

        var types = new RoslynTypeSystem(compilation);
        measure("runtime-contract-context", 100, () => new BindingContext(documents[0].Syntax, types, profile, options, default).Runtime.Services.Length);
        var members = compilation.GetTypeByMetadataName("Pipeline.Item")!.GetMembers();
        measure("accessibility-1000", 10, () =>
        {
            var accepted = 0;
            for (var i = 0; i < 1000; i++) if (types.IsAccessible(members[i % members.Length])) accepted++;
            return accepted;
        });
        var directiveRoot = XamlSyntaxTree.Parse("<Root xmlns:p='urn:p' xmlns:x='" + XamlNames.Language2006 + "' " +
            string.Join(" ", Enumerable.Range(0, 64).Select(i => "p:A" + i + "='v'")) + " x:Name='node'/>").Root!;
        var scope = NamespaceScope.Empty.Push(directiveRoot);
        measure("directive-lookups-1000", 5, () =>
        {
            var found = 0;
            for (var i = 0; i < 1000; i++) if (scope.Directive(directiveRoot, i % 2 == 0 ? "Name" : "Class") != null) found++;
            return found;
        });

        var loaderTrees = Enumerable.Range(0, 128).Select(i => CSharpSyntaxTree.ParseText("internal class Caller" + i +
            " { void Work() { " + string.Concat(Enumerable.Repeat("System.GC.KeepAlive(this);", 32)) +
            (i % 16 == 0 ? "Pipeline.Loader.Load(this);" : "") + " } }", parse, "Caller" + i + ".cs"));
        var loaderCompilation = compilation.AddSyntaxTrees(loaderTrees);
        var empty = new XamlProjectCompiler().Compile(Array.Empty<XamlProjectDocument>(), loaderCompilation);
        var loader = new XamlLoaderAdapterCompiler();
        var configuration = new XamlLoaderConfiguration("Pipeline.Loader", "Load");
        var integrated = loader.Compile(loaderCompilation, empty, configuration);
        Require(integrated.Diagnostics.IsEmpty && integrated.Source.Length != 0, "Loader fixture failed");
        measure("loader-warm-128-trees", 10, () => loader.Compile(loaderCompilation, empty, configuration).Source.Length);

        var additional = new Input("Driver.xaml", Markup("initial", 1000));
        var replacement = new Input(additional.Path, Markup("edited", 1000));
        GeneratorDriver NewDriver(IEnumerable<AdditionalText> files, bool enabled = true) => CSharpGeneratorDriver.Create(
            new[] { new XamlIncrementalGenerator().AsSourceGenerator() }, files, parse, new Config(enabled));
        var driver = NewDriver(new[] { additional }).RunGenerators(compilation);
        Require(driver.GetRunResult().Diagnostics.IsEmpty, "Generator fixture failed");
        var driverSource = driver.GetRunResult().GeneratedTrees.Single().GetText().ToString();
        measure("generator-cold-one-file-1000", 1, () => NewDriver(new[] { additional }).RunGenerators(compilation).GetRunResult().GeneratedTrees.Length);
        measure("generator-noop-one-file-1000", 20, () => { driver = driver.RunGenerators(compilation); return driver.GetRunResult().GeneratedTrees.Length; });
        AdditionalText current = additional;
        measure("generator-edit-one-file-1000", 2, () =>
        {
            var next = ReferenceEquals(current, additional) ? replacement : additional;
            driver = driver.ReplaceAdditionalText(current, next).RunGenerators(compilation); current = next;
            return driver.GetRunResult().GeneratedTrees.Length;
        });
        var disabledInputs = documents.Select(document => new Input(document.LogicalPath, document.Syntax.Text)).ToArray();
        measure("generator-disabled-64-files", 10, () => NewDriver(disabledInputs, false).RunGenerators(compilation).GetRunResult().GeneratedTrees.Length);

        var boxes = Enumerable.Range(0, 1000).Select(_ => new Box()).ToArray();
        var keys = Enumerable.Range(0, boxes.Length).Select(i => "n" + i).ToArray();
        var table = new XamlPropertyTable(new[] { typeof(int) }, static (target, _) => ((Box)target).Value,
            static (target, _, value) => ((Box)target).Value = (int)value!);
        measure("runtime-table-register-1000", 10, () =>
        {
            using var session = new XamlRuntimeSession();
            for (var i = 0; i < boxes.Length; i++) table.Register(session, keys[i], "Value", boxes[i], 0);
            return boxes.Length;
        });
        var getters = boxes.Select(box => (Func<int>)(() => box.Value)).ToArray();
        var setters = boxes.Select(box => (Action<int>)(value => box.Value = value)).ToArray();
        measure("runtime-delegate-register-1000", 10, () =>
        {
            using var session = new XamlRuntimeSession();
            for (var i = 0; i < boxes.Length; i++) session.RegisterProperty(keys[i], "Value", getters[i], setters[i]);
            return boxes.Length;
        });
        using var mutations = new XamlRuntimeSession();
        for (var i = 0; i < boxes.Length; i++) table.Register(mutations, keys[i], "Value", boxes[i], 0);
        var updates = keys.Select(key => new XamlPropertyUpdate(key, "Value", 42)).ToArray();
        measure("runtime-edit-batch-1000", 10, () => mutations.Apply(mutations.Revision, updates).Applied ? 1 : 0);
        Require(boxes.All(box => box.Value == 42), "Runtime updates targeted the wrong objects");
        // Deterministic generated output, mapping values and work counts—not object
        // identities or timing—join the cross-revision semantic signature.
        return Signature(cold) + Signature(warm) + Signature(edit) + JsonSerializer.Serialize(new[] { warm.Statistics, edit.Statistics }) +
            integrated.Source + driverSource + string.Join(",", boxes.Select(box => box.Value));
    }

    private static string Signature(XamlProjectCompilation project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(project.Documents.Select(document => new { document.Input.LogicalPath, document.Output.Source,
            document.Output.SourceMappings, document.Output.Diagnostics })))));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Box { public int Value; }
    private sealed class Input(string path, string content) : AdditionalText
    {
        private readonly SourceText _text = SourceText.From(content);
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
    private sealed class Config(bool enabled) : AnalyzerConfigOptionsProvider
    {
        private sealed class Options(bool enabled) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, out string value)
            { value = enabled ? "true" : "false"; return key == "build_property.XamlGEnabled"; }
        }
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(enabled);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }
}
