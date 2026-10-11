using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;

/// <summary>Same graph-only IR and pre-bound delegates on both revisions. Cold
/// scans include a fresh root/document; warm validation includes array cloning.
/// These synthetic graphs are NOT claimed to be executable generated programs.</summary>
internal static class ResourceDiscoveryProbe
{
    public static Result Run(Action<string, int, Func<int>> measure)
    {
        var graph = typeof(XamlProjectCompiler).Assembly.GetType("XamlG.CSharp.Resources.XamlResourceGraph", throwOnError: true)!;
        var references = (Func<BoundDocument, CancellationToken, ImmutableArray<BoundResourceExpression>>)graph
            .GetMethod("References", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<BoundDocument, CancellationToken, ImmutableArray<BoundResourceExpression>>));
        var validate = (Func<BoundDocument[], CancellationToken, BoundDocument[]>)graph.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<BoundDocument[], CancellationToken, BoundDocument[]>));
        var validateEmissions = (Action<BoundDocument[], XamlEmissionResult[], CancellationToken>)graph.GetMethod("ValidateEmissions", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<BoundDocument[], XamlEmissionResult[], CancellationToken>));
        var metadata = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ResourceDiscoveryProbe", new[] { CSharpSyntaxTree.ParseText(
            "namespace DiscoveryProbe { public class Node { public Node Child {get;set;} } }", new CSharpParseOptions(LanguageVersion.Preview)) },
            metadata, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:DiscoveryProbe'/>", "Graph.xaml"), compilation);
        Require(seed.Success && seed.Root != null, "Model binding failed");
        var root = seed.Root!;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        var array = compilation.CreateArrayTypeSymbol(root.Type);
        var signature = new StringBuilder();
        var sizes = new List<SnapshotSize>();

        foreach (var count in new[] { 1, 32, 1024 })
        foreach (var shape in new[] { "none", "unique", "duplicate" })
        {
            var expressions = Enumerable.Range(0, count).Select(i => shape == "none"
                ? (BoundExpression)new BoundConstantExpression(null, root.Type, new(i, 1))
                : Resource(shape == "unique" ? "u" + i : "u0", i)).ToArray();
            var document = Document(expressions);
            var name = "flat-" + shape + "-" + count;
            Cold(name, document, count == 1024 ? 3 : 30);
            var targets = Enumerable.Range(0, shape == "unique" ? count : 1)
                .Select(i => seed with { Options = seed.Options with { ResourceUri = "u" + i } });
            var documents = new[] { document }.Concat(targets).ToArray();
            var warmed = validate((BoundDocument[])documents.Clone(), default);
            Require(warmed.All(d => d.Success), "Acyclic warm graph failed: " + name);
            measure("resource-discovery-warm-validate-" + name, count == 1024 ? 3 : 30,
                () => validate((BoundDocument[])documents.Clone(), default).Length);
            SnapshotGraph(name, documents);
        }

        foreach (var depth in new[] { 8, 16 })
        foreach (var hasResource in new[] { false, true })
        {
            BoundExpression value = hasResource ? Resource("u0", 31) : new BoundConstantExpression(null, root.Type, default);
            for (var i = 0; i < depth; i++) value = new BoundArrayExpression([value, value], array, default);
            var name = "shared-" + (hasResource ? "resource-" : "empty-") + depth;
            var document = Document(value);
            Cold(name, document, depth == 8 ? 10 : 1);
            SnapshotGraph(name, [document, seed with { Options = seed.Options with { ResourceUri = "u0" } }]);
        }

        foreach (var depth in new[] { 32, 4096 })
        {
            BoundExpression value = Resource("u0", 19);
            for (var i = 0; i < depth; i++) value = new BoundCastExpression(value, root.Type, default);
            Cold("unary-" + depth, Document(value), depth == 32 ? 20 : 2);
        }

        foreach (var depth in new[] { 4, 10 })
        foreach (var hasResource in new[] { false, true })
        {
            var position = 0;
            // Deliberately distinct composites expose admission/identity-set costs
            // on unshared trees. This untimed generator is not the tested traversal.
            BoundExpression Tree(int level) => level == 0
                ? hasResource ? Resource("u0", position++) : new BoundConstantExpression(null, root.Type, default)
                : new BoundArrayExpression([Tree(level - 1), Tree(level - 1)], array, default);
            Cold("unshared-" + (hasResource ? "resource-" : "empty-") + depth, Document(Tree(depth)), depth == 4 ? 20 : 2);
        }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))), sizes);

        BoundResourceExpression Resource(string uri, int position) => new(new(uri, root.Type, uri, "Generated", null), new(position, 1));
        BoundDocument Document(params BoundExpression[] values) => seed with
        {
            Options = seed.Options with { ResourceUri = "caller" },
            Root = root with { Assignments = values.Select(value => (BoundAssignment)new BoundSetAssignment(member, value, default)).ToImmutableArray() }
        };
        void Cold(string name, BoundDocument document, int operations)
        {
            var snapshot = references(document, default);
            // Internal representation is intentionally compacted. Compare the exact
            // first descriptor/span per ordinal URI, NOT the discarded duplicate count.
            var selected = snapshot.GroupBy(r => r.Resource.Uri, StringComparer.Ordinal).Select(group => group.First()).ToArray();
            signature.Append(name).Append(JsonSerializer.Serialize(selected.Select(r => new
            {
                r.Resource.Uri, r.Resource.LocalDocumentId, r.Resource.GeneratedNamespace,
                RootType = r.Resource.RootType.ToDisplayString(), r.Span, r.SourceInfoSpan, r.SuppressSourceInfo
            })));
            sizes.Add(new(name, snapshot.Length, selected.Length));
            measure("resource-discovery-cold-" + name, operations, () =>
            {
                var fresh = document with { Root = document.Root! with { } };
                var result = references(fresh, default);
                return result.IsEmpty ? 0 : result[0].Span.Start;
            });
        }
        void SnapshotGraph(string name, BoundDocument[] documents)
        {
            var actual = validate((BoundDocument[])documents.Clone(), default);
            signature.Append(name).Append(JsonSerializer.Serialize(actual.Select(d => new { d.Success, d.Diagnostics })));
            var outputs = documents.Select((d, i) => new XamlEmissionResult("g" + i, i == documents.Length - 1 ? "" : "source",
                "Factory" + i, "Build", "Populate", [], [])).ToArray();
            var failed = (BoundDocument[])documents.Clone();
            validateEmissions(failed, outputs, default);
            signature.Append(JsonSerializer.Serialize(failed.Select(d => new { d.Success, d.Diagnostics })));
            signature.Append(JsonSerializer.Serialize(outputs.Select(o => new { o.Success, o.Source, o.Diagnostics, o.SourceMappings })));
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal sealed record SnapshotSize(string Workload, int RetainedReferences, int UniqueUris);
    internal sealed record Result(string SemanticSignature, List<SnapshotSize> Snapshots);
}
