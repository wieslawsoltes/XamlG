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

/// <summary>Graph-only topology probes. Both revisions use the same existing
/// validator delegates; no candidate-only storage API enters the measurement.</summary>
internal static class ResourceAdjacencyProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var graph = typeof(XamlProjectCompiler).Assembly.GetType("XamlG.CSharp.Resources.XamlResourceGraph", true)!;
        var validate = (Func<BoundDocument[], CancellationToken, BoundDocument[]>)graph.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<BoundDocument[], CancellationToken, BoundDocument[]>));
        var backend = (Action<BoundDocument[], XamlEmissionResult[], CancellationToken>)graph.GetMethod("ValidateEmissions", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<BoundDocument[], XamlEmissionResult[], CancellationToken>));
        var metadata = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ResourceAdjacencyProbe", [CSharpSyntaxTree.ParseText(
            "namespace Adjacency { public class Node { public Node Child {get;set;} } }", new CSharpParseOptions(LanguageVersion.Preview))],
            metadata, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Adjacency'/>", "Graph.xaml"), compilation);
        if (!seed.Success || seed.Root == null) throw new InvalidOperationException("Graph model binding failed");
        var root = seed.Root;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        // Internal helper metadata is accessed only during untimed fixture setup
        // and validation; the probe is intentionally not a compiler friend assembly.
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var shared = typeof(XamlEmissionResult).GetProperty("SharedSources", members)!;
        var layout = typeof(XamlEmissionResult).GetProperty("PropertyLayout", members)!;
        var helperType = shared.PropertyType.GenericTypeArguments[0];
        var helperConstructor = helperType.GetConstructor([typeof(string), typeof(string)])!;
        var appendHelper = shared.PropertyType.GetMethod("Add", [helperType])!;
        var signature = new StringBuilder();
        foreach (var count in new[] { 32, 1024 })
        foreach (var shape in new[] { "fanin", "islands", "lattice", "dense-blocks" })
        {
            // Every local target is later in document order. This makes a simple
            // untimed descending closure independent of the production queue.
            var targets = Enumerable.Range(0, count).Select(i => Targets(i).ToArray()).ToArray();
            var documents = Enumerable.Range(0, count).Select(i => seed with
            {
                Options = seed.Options with { ResourceUri = "u" + i },
                Root = root with { Assignments = targets[i].Select((target, ordinal) => (BoundAssignment)new BoundSetAssignment(member,
                    new BoundResourceExpression(new("u" + target, root.Type, "source" + target, "Generated", null), new(i * 64 + ordinal, 1)), default)).ToImmutableArray() }
            }).ToArray();
            var valid = validate((BoundDocument[])documents.Clone(), default);
            if (valid.Any(document => !document.Success)) throw new InvalidOperationException("An acyclic topology was rejected");
            var expectedFailures = Enumerable.Range(0, count).Select(i => i == count / 3 || i == count * 2 / 3 || i == count - 1).ToArray();
            var outputs = Enumerable.Range(0, count).Select(i =>
            {
                var output = new XamlEmissionResult("g" + i, expectedFailures[i] ? "" : "source" + i,
                    "Factory" + i, "Build", "Populate", [], [new(new(1, 2), new(i, 1), "Graph.xaml")]);
                var helper = helperConstructor.Invoke(["Helper" + i, "source"]);
                shared.SetValue(output, appendHelper.Invoke(shared.GetValue(output), [helper]));
                layout.SetValue(output, "layout" + i);
                return output;
            }).ToArray();
            for (var i = count - 1; i >= 0; i--)
                expectedFailures[i] |= targets[i].Any(target => expectedFailures[target]);
            var checkedDocuments = (BoundDocument[])documents.Clone();
            var checkedOutputs = (XamlEmissionResult[])outputs.Clone();
            backend(checkedDocuments, checkedOutputs, default);
            for (var i = 0; i < count; i++)
            {
                if (checkedOutputs[i].Success == expectedFailures[i]) throw new InvalidOperationException("Unexpected backend failure closure");
                if (!Equals(shared.GetValue(outputs[i]), shared.GetValue(checkedOutputs[i])) ||
                    !Equals(layout.GetValue(outputs[i]), layout.GetValue(checkedOutputs[i])))
                    throw new InvalidOperationException("Backend failure changed helper ownership");
                signature.Append(JsonSerializer.Serialize(shared.GetValue(checkedOutputs[i]), shared.PropertyType));
                signature.Append(JsonSerializer.Serialize(layout.GetValue(checkedOutputs[i])));
            }
            signature.Append(shape).Append(count).Append(JsonSerializer.Serialize(targets));
            signature.Append(JsonSerializer.Serialize(valid.Select(document => new { document.Success, document.Diagnostics })));
            signature.Append(JsonSerializer.Serialize(checkedDocuments.Select(document => new { document.Success, document.Diagnostics })));
            signature.Append(JsonSerializer.Serialize(checkedOutputs));
            var operations = count == 32 ? 20 : 3;
            measure("resource-packed-warm-" + shape + "-" + count, operations,
                () => validate((BoundDocument[])documents.Clone(), default).Length);
            measure("resource-packed-backend-" + shape + "-" + count, operations, () =>
            {
                var currentDocuments = (BoundDocument[])documents.Clone();
                var currentOutputs = (XamlEmissionResult[])outputs.Clone();
                backend(currentDocuments, currentOutputs, default);
                return currentOutputs[0].Source.Length;
            });
            if (shape == "fanin")
                measure("resource-packed-cold-" + shape + "-" + count, operations, () =>
                {
                    var fresh = documents.Select(document => document with { Root = document.Root! with { } }).ToArray();
                    return validate(fresh, default).Length;
                });

            IEnumerable<int> Targets(int index) => shape switch
            {
                "fanin" => index < count - 1 ? new[] { count - 1 } : [],
                "islands" => index % 8 == 0 && index + 1 < count ? new[] { index + 1 } : [],
                "lattice" => Enumerable.Range(index + 1, Math.Min(4, count - index - 1)),
                _ => Enumerable.Range(index + 1, Math.Min(count, (index / 16 + 1) * 16) - index - 1)
            };
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }
}
