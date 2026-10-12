using System.Collections.Immutable;
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

internal static class TypeCandidateProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var names = Enumerable.Range(0, 256).Select(i => "Item" + i).ToArray();
        var model = "using System.Collections.Generic; using XamlG.Runtime; namespace CandidateProbe { " +
            "public class Root { [Content] public List<object> Children {get;} = new(); } " +
            string.Concat(names.Select(name => "public class " + name + " { public string Text {get;set;} = \"\"; }")) + "}";
        CSharpCompilation Compilation(string name, string source, params MetadataReference[] extra) =>
            CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, parse)], references.Concat(extra),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        PortableExecutableReference Image(CSharpCompilation value)
        {
            using var bytes = new MemoryStream();
            var emitted = value.Emit(bytes);
            Require(emitted.Success, string.Join("\n", emitted.Diagnostics));
            return MetadataReference.CreateFromImage(bytes.ToArray());
        }
        const string external = "namespace CandidateExternal { public class Item {} public class Box<T> {} internal class Hidden {} }";
        var first = Image(Compilation("CandidateZ", external));
        var second = Image(Compilation("CandidateA", external));
        var facade = Image(Compilation("CandidateFacade", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(CandidateExternal.Item))]", first));
        var compilation = Compilation("TypeCandidateProbe", model, first, second, facade);
        var configuration = new XamlTypeSystemConfiguration
        {
            NamespaceMappings = [new("urn:ambiguous", "CandidateExternal"),
                new("urn:forward", "CandidateExternal", "CandidateZ"), new("urn:forward", "CandidateExternal", "CandidateFacade"),
                new("urn:prefixes", "CandidateProbe"), new("urn:prefixes", "Missing.Namespace"),
                new("urn:prefixes", "CandidateProbe", "TypeCandidateProbe")]
        };
        var types = new RoslynTypeSystem(compilation, configuration);
        // Bind the existing method once, outside timing. Both revisions execute
        // the same delegate without reflection invocation or name construction in
        // a measured batch. This isolates first-resolution work, not cache hits.
        var core = (Func<string, string, int, TypeResolution>)typeof(RoslynTypeSystem)
            .GetMethod("ResolveCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<string, string, int, TypeResolution>), types);
        var cases = new (string Name, string Namespace, string Type, int Arity)[]
        {
            ("single-hit", "clr-namespace:CandidateProbe;assembly=TypeCandidateProbe", "Item0", 0),
            ("single-miss", "clr-namespace:CandidateProbe;assembly=TypeCandidateProbe", "Missing", 0),
            ("untargeted-hit", "using:CandidateProbe", "Item0", 0),
            ("untargeted-miss", "using:CandidateProbe", "Missing", 0),
            ("ambiguous", "urn:ambiguous", "Item", 0),
            ("generic-ambiguous", "urn:ambiguous", "Box", 1),
            ("forwarded-duplicate", "urn:forward", "Item", 0),
            ("prefix-switch", "urn:prefixes", "Item0", 0)
        };
        using var signature = new MemoryStream();
        using (var writer = new BinaryWriter(signature, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var item in cases)
            {
                var result = core(item.Namespace, item.Type, item.Arity);
                writer.Write(item.Name);
                writer.Write(result.Candidates.Length);
                foreach (var candidate in result.Candidates)
                {
                    writer.Write(candidate.ContainingAssembly.Identity.ToString());
                    writer.Write(candidate.MetadataName());
                }
                writer.Write(result.Type?.MetadataName() ?? "");
                measure("type-candidate-" + item.Name, 1000, () => core(item.Namespace, item.Type, item.Arity).Candidates.Length);
            }
            Require(core("urn:forward", "Item", 0).Candidates.Length == 1, "Forwarder was not deduplicated.");
            Require(core("urn:ambiguous", "Item", 0).Candidates.Length == 2, "Ambiguity was lost.");
            foreach (var count in new[] { 1, 64, 256 })
            {
                var batch = names.Take(count).ToArray();
                measure("type-resolve-public-cold-" + count, 5, () =>
                {
                    var symbols = new RoslynTypeSystem(compilation, configuration);
                    var found = 0;
                    foreach (var name in batch) found += symbols.Resolve("using:CandidateProbe", name).Candidates.Length;
                    return found;
                });
            }
            foreach (var name in names) Require(types.Resolve("using:CandidateProbe", name).Type != null, "Missing fixture type.");
            measure("type-resolve-public-warm-256", 100, () =>
            {
                var found = 0;
                foreach (var name in names) found += types.Resolve("using:CandidateProbe", name).Candidates.Length;
                return found;
            });
            foreach (var count in new[] { 32, 256 })
            {
                var source = "<Root xmlns='using:CandidateProbe'>" +
                    string.Concat(names.Take(count).Select(name => "<" + name + " Text='value'/>")) + "</Root>";
                var syntax = XamlSyntaxTree.Parse(source, "Candidates.xaml");
                var bound = new XamlCompiler().Bind(syntax, types, XamlFrameworkProfile.Portable);
                Require(bound.Success, string.Join("\n", bound.Diagnostics));
                var output = new CSharpEmitter().Emit(bound);
                Require(output.Success, string.Join("\n", output.Diagnostics));
                using var bytes = new MemoryStream();
                var emitted = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parse, output.HintName)).Emit(bytes);
                Require(emitted.Success, string.Join("\n", emitted.Diagnostics));
                writer.Write(output.Source);
                writer.Write(JsonSerializer.Serialize(output.SourceMappings));
                writer.Write(JsonSerializer.Serialize(output.Diagnostics));
                measure("type-candidate-bind-cold-" + count, 3, () =>
                    new XamlCompiler().Bind(syntax, new RoslynTypeSystem(compilation, configuration), XamlFrameworkProfile.Portable).Symbols.Length);
                if (count == 256)
                    measure("type-candidate-bind-warm-256", 3, () =>
                        new XamlCompiler().Bind(syntax, types, XamlFrameworkProfile.Portable).Symbols.Length);
            }
        }
        return Convert.ToHexString(SHA256.HashData(signature.ToArray()));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
