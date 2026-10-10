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

/// <summary>Same direct graph delegates and inputs compiled against both revisions.
/// These are graph-only IR fixtures, not purported generated/executable programs.</summary>
internal static class ResourceGraphProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var graph = typeof(XamlProjectCompiler).Assembly.GetType("XamlG.CSharp.Resources.XamlResourceGraph", throwOnError: true)!;
        var validate = (Func<BoundDocument[], CancellationToken, BoundDocument[]>)graph.GetMethod("Validate", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<BoundDocument[], CancellationToken, BoundDocument[]>));
        var validateEmissions = (Action<BoundDocument[], XamlEmissionResult[], CancellationToken>)graph.GetMethod("ValidateEmissions", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate(typeof(Action<BoundDocument[], XamlEmissionResult[], CancellationToken>));
        // Reflection, model compilation, binding and graph construction are untimed.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ResourceGraphProbe",
            new[] { CSharpSyntaxTree.ParseText("namespace GraphProbe { public class Node { public Node Child {get;set;} } }", new CSharpParseOptions(LanguageVersion.Preview)) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:GraphProbe'/>", "Graph.xaml"), compilation);
        Require(seed.Success && seed.Root != null, "Graph model binding failed");
        var root = seed.Root!;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        var signature = new StringBuilder();
        foreach (var count in new[] { 32, 1024 })
        foreach (var shape in new[] { "none", "chain", "fanout", "duplicates", "cycle" })
        {
            var uris = Enumerable.Range(0, count).Select(i => "u" + i).ToArray();
            var documents = Enumerable.Range(0, count).Select(i => seed with
            {
                Options = seed.Options with { ResourceUri = uris[i] },
                Root = root with { Assignments = Targets(i).Select((target, ordinal) => (BoundAssignment)new BoundSetAssignment(member,
                    new BoundResourceExpression(new(target, root.Type, target, "Generated", null), new(i * 64 + ordinal, 1)), default)).ToImmutableArray() }
            }).ToArray();
            var result = validate((BoundDocument[])documents.Clone(), default);
            Require(result.All(document => document.Success == (shape != "cycle")), "Unexpected graph validity: " + shape);
            signature.Append(shape).Append(count).Append(JsonSerializer.Serialize(result.Select(document => new { document.Success, document.Diagnostics })));
            var operations = count == 32 ? 20 : 3;
            measure("resource-graph-warm-" + shape + "-" + count, operations, () => validate((BoundDocument[])documents.Clone(), default).Length);
            if (shape is "none" or "chain")
                measure("resource-graph-cold-" + shape + "-" + count, operations, () =>
                {
                    // Fresh immutable roots prevent reference-snapshot cache hits.
                    // Clone and first-scan costs are included equally for both revisions.
                    var fresh = documents.Select(document => document with { Root = document.Root! with { } }).ToArray();
                    return validate(fresh, default).Length;
                });
            if (shape is "none" or "chain" or "fanout")
            {
                var outputs = Enumerable.Range(0, count).Select(i => new XamlEmissionResult("g" + i, i == count - 1 ? "" : "source" + i,
                    "Factory" + i, "Build", "Populate", ImmutableArray<XamlDiagnostic>.Empty,
                    ImmutableArray.Create(new XamlSourceMapping(new(1, 2), new(i, 1), "Graph.xaml")))).ToArray();
                var checkedDocuments = (BoundDocument[])documents.Clone();
                var checkedOutputs = (XamlEmissionResult[])outputs.Clone();
                validateEmissions(checkedDocuments, checkedOutputs, default);
                var failures = shape == "none" ? 1 : shape == "chain" ? count : 2;
                Require(checkedOutputs.Count(output => !output.Success) == failures, "Wrong backend failure closure: " + shape);
                signature.Append(JsonSerializer.Serialize(checkedDocuments.Select(document => new { document.Success, document.Diagnostics })));
                signature.Append(JsonSerializer.Serialize(checkedOutputs));
                measure("resource-graph-backend-" + shape + "-" + count, operations, () =>
                {
                    var currentDocuments = (BoundDocument[])documents.Clone();
                    var currentOutputs = (XamlEmissionResult[])outputs.Clone();
                    validateEmissions(currentDocuments, currentOutputs, default);
                    return currentOutputs[0].Source.Length;
                });
            }

            IEnumerable<string> Targets(int index) => shape switch
            {
                "none" => new[] { "external" },
                "fanout" => index == 0 ? uris.Skip(1) : Array.Empty<string>(),
                "cycle" => new[] { uris[(index + 1) % count] },
                "duplicates" => index == count - 1 ? Array.Empty<string>() : Enumerable.Repeat(uris[index + 1], 32),
                _ => index == count - 1 ? Array.Empty<string>() : new[] { uris[index + 1] }
            };
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
