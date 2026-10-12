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

internal static class NamespaceTargetProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        const string model = "using System.Collections.Generic; using XamlG.Runtime; namespace TargetProbe { " +
            "public class Root { [Content] public List<Item> Children {get;} = new(); } " +
            "public class Item { public string Text {get;set;} = \"\"; } }";
        var compilation = CSharpCompilation.Create("NamespaceTargetProbe", [CSharpSyntaxTree.ParseText(model, parse)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var signature = new MemoryStream();
        using (var writer = new BinaryWriter(signature, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var count in new[] { 8, 32, 64, 1024 })
            {
                var names = Enumerable.Range(0, count).Select(i => "urn:target" + i).ToArray();
                var configuration = new XamlTypeSystemConfiguration
                {
                    XmlnsDefinitionAttributes = [],
                    NamespaceMappings = names.Select(ns => new XmlNamespaceMapping(ns, "TargetProbe", compilation.AssemblyName)).ToImmutableArray()
                };
                var types = new RoslynTypeSystem(compilation, configuration);
                var core = Core(types);
                // Explicitly finish admission/warmup before isolated core timings.
                for (var i = 0; i < 8; i++) core(names[i % count]);
                foreach (var ns in names.Append("urn:absent"))
                {
                    var values = core(ns);
                    writer.Write(ns); writer.Write(values.Length);
                    foreach (var target in values)
                    { writer.Write(target.Prefix); writer.Write(target.Assembly.Identity.ToString()); }
                }
                measure("namespace-target-core-hit-" + count, 1000, () => core(names[count - 1]).Length);
                measure("namespace-target-core-miss-" + count, 1000, () => core("urn:absent").Length);
                foreach (var queries in new[] { 1, Math.Min(count, 256) })
                    measure("namespace-target-public-cold-" + count + "-q" + queries, 5, () =>
                    {
                        var fresh = new RoslynTypeSystem(compilation, configuration);
                        var found = 0;
                        for (var i = 0; i < queries; i++) found += fresh.Resolve(names[i], "Item").Candidates.Length;
                        return found;
                    });
                if (count >= 64)
                {
                    var size = Math.Min(count, 256);
                    var source = "<Root xmlns='clr-namespace:TargetProbe'>" +
                        string.Concat(names.Take(size).Select(ns => "<p:Item xmlns:p='" + ns + "' Text='value'/>")) + "</Root>";
                    var syntax = XamlSyntaxTree.Parse(source, "Targets.xaml");
                    var bound = new XamlCompiler().Bind(syntax, types, XamlFrameworkProfile.Portable);
                    Require(bound.Success, string.Join("\n", bound.Diagnostics));
                    var output = new CSharpEmitter().Emit(bound);
                    Require(output.Success, string.Join("\n", output.Diagnostics));
                    using var image = new MemoryStream();
                    var emitted = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(output.Source, parse, output.HintName)).Emit(image);
                    Require(emitted.Success, string.Join("\n", emitted.Diagnostics));
                    writer.Write(output.Source); writer.Write(JsonSerializer.Serialize(output.SourceMappings));
                    writer.Write(JsonSerializer.Serialize(output.Diagnostics));
                    measure("namespace-target-bind-cold-" + count, 3, () =>
                        new XamlCompiler().Bind(syntax, new RoslynTypeSystem(compilation, configuration), XamlFrameworkProfile.Portable).Symbols.Length);
                }
            }
            // One URI with many nonidentical mappings exercises the link-array
            // cost and order rather than only favorable all-unique map keys.
            var grouped = new RoslynTypeSystem(compilation, new()
            {
                XmlnsDefinitionAttributes = [],
                NamespaceMappings = Enumerable.Range(0, 256).Select(i =>
                    new XmlNamespaceMapping("urn:group", "TargetProbe", "NamespaceTargetProbe, Version=" + i)).ToImmutableArray()
            });
            var groupCore = Core(grouped);
            for (var i = 0; i < 8; i++) groupCore("urn:group");
            var group = groupCore("urn:group");
            Require(group.Length == 256, "Repeated assembly probes were lost.");
            writer.Write(group.Length);
            foreach (var target in group) { writer.Write(target.Prefix); writer.Write(target.Assembly.Identity.ToString()); }
            measure("namespace-target-grouped-256", 100, () => groupCore("urn:group").Length);
            measure("namespace-target-grouped-miss-256", 1000, () => groupCore("urn:absent").Length);
            foreach (var unique in new[] { 24, 1000 })
            {
                // Deterministic URI values on both revisions/processes. Native
                // collision tests use runtime hashes, but benchmarks never do.
                var mappings = Enumerable.Range(0, 1024).Select(i => new XmlNamespaceMapping(
                    i < unique ? "urn:mixed" + i : "urn:hub", "TargetProbe", "NamespaceTargetProbe, Version=" + i)).ToImmutableArray();
                var configuration = new XamlTypeSystemConfiguration { XmlnsDefinitionAttributes = [], NamespaceMappings = mappings };
                var names = Enumerable.Range(0, Math.Min(unique, 128)).Select(i => "urn:mixed" + i).Append("urn:hub").ToArray();
                var types = new RoslynTypeSystem(compilation, configuration);
                var core = Core(types);
                for (var i = 0; i < 8; i++) core(names[i]);
                foreach (var ns in names)
                {
                    var targets = core(ns);
                    writer.Write(ns); writer.Write(targets.Length);
                    foreach (var target in targets)
                    { writer.Write(target.Prefix); writer.Write(target.Assembly.Identity.ToString()); }
                }
                measure("namespace-target-mixed-warm-unique" + unique, 20, () =>
                {
                    var count = 0;
                    foreach (var ns in names) count += core(ns).Length;
                    return count;
                });
                measure("namespace-target-mixed-cold-unique" + unique, 5, () =>
                {
                    var fresh = new RoslynTypeSystem(compilation, configuration);
                    var found = 0;
                    foreach (var ns in names) found += fresh.Resolve(ns, "Item").Candidates.Length;
                    return found;
                });
            }
        }
        return Convert.ToHexString(SHA256.HashData(signature.ToArray()));
    }

    private static Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>> Core(RoslynTypeSystem types) =>
        (Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>>)typeof(RoslynTypeSystem)
            .GetMethod("ResolveNamespaceTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<string, ImmutableArray<(string Prefix, IAssemblySymbol Assembly)>>), types);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
