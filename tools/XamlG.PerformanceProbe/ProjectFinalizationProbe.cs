using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;

/// <summary>Whole public project compilation, not a claim to isolate finalization.
/// Same fixture and call sites run against baseline and candidate without reflection shims.</summary>
internal static class ProjectFinalizationProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        const string model = """
            namespace FinalizationModel {
                public class Item { public int Value { get; set; } }
                public partial class Window : Item { }
            }
            """;
        var compilation = CSharpCompilation.Create("FinalizationProbe", new[] { CSharpSyntaxTree.ParseText(model, parse, "Model.cs") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var options = new XamlCompilerOptions { GenerateBuildMethod = false, MaxDegreeOfParallelism = 1 };
        var signature = new StringBuilder();
        foreach (var count in new[] { 1, 32, 256 })
        foreach (var property in new[] { false, true })
        {
            var shape = property ? "shared" : "empty";
            var documents = Enumerable.Range(0, count).Select(i => Document(i, property)).ToArray();
            var compiler = new XamlProjectCompiler();
            var initial = compiler.Compile(documents, compilation, options: options);
            Require(initial.Success, "Finalization fixture did not bind: " + shape);
            RequireCompiled(initial);
            var warm = compiler.Compile(documents, compilation, options: options);
            Require(warm.Statistics == new XamlProjectStatistics(0, count, 0, count), "Warm finalization performed binding or emission");
            Require(Signature(initial) == Signature(warm), "Warm project source differs");
            signature.Append(Signature(initial)).Append(JsonSerializer.Serialize(warm.Statistics));
            var operations = count == 1 ? 10 : 2;
            measure("project-finalize-noop-" + shape + "-" + count, operations,
                () => compiler.Compile(documents, compilation, options: options).Documents.Length);
            measure("project-finalize-cold-" + shape + "-" + count, count == 1 ? 5 : 1,
                () => new XamlProjectCompiler().Compile(documents, compilation, options: options).Documents.Length);
            if (count == 1) continue;
            var reduced = documents.Skip(1).ToArray();
            var removed = compiler.Compile(reduced, compilation, options: options);
            var fresh = new XamlProjectCompiler().Compile(reduced, compilation, options: options);
            Require(removed.Success && Signature(removed) == Signature(fresh), "Ownership transfer differs from a fresh project");
            RequireCompiled(removed);
            signature.Append(Signature(removed));
            var useReduced = true;
            measure("project-finalize-owner-transfer-" + shape + "-" + count, 2,
                () => compiler.Compile((useReduced = !useReduced) ? reduced : documents, compilation, options: options).Documents.Length);
            if (!property) continue;
            var duplicates = Enumerable.Range(0, count).Select(i => Document(i, property: true, withClass: true) with
            { LogicalPath = "duplicate" + (i % 8) + ".xaml", ResourceUri = "avares://fixture/resource" + (i % 4) + ".xaml" }).ToArray();
            var invalid = new XamlProjectCompiler().Compile(duplicates, compilation, options: options);
            Require(!invalid.Success && invalid.Documents.All(d =>
                d.Document.Diagnostics.Any(diagnostic => diagnostic.Code == "XG3300") &&
                d.Document.Diagnostics.Any(diagnostic => diagnostic.Code == "XG2002")), "Duplicate declarations were not diagnosed");
            signature.Append(Signature(invalid));
            measure("project-finalize-duplicates-" + count, 1,
                () => new XamlProjectCompiler().Compile(duplicates, compilation, options: options).Documents.Length);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));

        void RequireCompiled(XamlProjectCompilation project)
        {
            // Untimed. Every successful generated fixture is actually compiled.
            using var stream = new MemoryStream();
            var result = compilation.AddSyntaxTrees(project.Documents.Select(d => CSharpSyntaxTree.ParseText(d.Output.Source, parse))).Emit(stream);
            Require(result.Success, "Generated finalization fixture failed: " + string.Join("\n", result.Diagnostics));
        }
    }

    private static XamlProjectDocument Document(int index, bool property, bool withClass = false)
    {
        var path = "View" + index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".xaml";
        return new(XamlSyntaxTree.Parse("<Item xmlns='clr-namespace:FinalizationModel' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
            (withClass ? " x:Class='FinalizationModel.Window'" : "") + (property ? " Value='12'" : "") + "/>", path), path);
    }
    private static string Signature(XamlProjectCompilation project) => JsonSerializer.Serialize(project.Documents.Select(d => new
    { d.Input.LogicalPath, d.ResourceUri, d.Document.IsSkipped, DocumentDiagnostics = d.Document.Diagnostics,
        d.Output.Source, d.Output.SourceMappings, d.Output.Diagnostics }));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
