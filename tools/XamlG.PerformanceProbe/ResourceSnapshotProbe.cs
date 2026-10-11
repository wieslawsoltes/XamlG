using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Compiler.Resources;
using XamlG.CSharp.Resources;
using XamlG.Runtime;
using XamlG.Syntax;

/// <summary>Identical public-signature delegate on both revisions. Cold batches
/// include fresh root/document records; warm batches reuse the exact live root.</summary>
internal static class ResourceSnapshotProbe
{
    public static string Run(Action<string, int, Func<int>> measure)
    {
        var graph = typeof(XamlProjectCompiler).Assembly.GetType("XamlG.CSharp.Resources.XamlResourceGraph", true)!;
        var references = (Func<BoundDocument, CancellationToken, ImmutableArray<BoundResourceExpression>>)graph
            .GetMethod("References", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<BoundDocument, CancellationToken, ImmutableArray<BoundResourceExpression>>));
        var metadata = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(XamlRuntimeContext).Assembly.Location).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("ResourceSnapshotProbe", [CSharpSyntaxTree.ParseText(
            "namespace SnapshotProbe { public class Node { public Node Child {get;set;} } }", new CSharpParseOptions(LanguageVersion.Preview))],
            metadata, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var seed = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:SnapshotProbe'/>", "Snapshot.xaml"), compilation);
        if (!seed.Success || seed.Root == null) throw new InvalidOperationException("Snapshot model binding failed");
        var root = seed.Root;
        var property = (IPropertySymbol)root.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type, property.GetMethod, property.SetMethod, default);
        var signature = new StringBuilder();
        foreach (var shape in new[] { "empty", "negative", "positive" })
        foreach (var cancellable in new[] { false, true })
        {
            using var source = new CancellationTokenSource();
            var token = cancellable ? source.Token : default;
            BoundExpression value = shape == "positive"
                ? new BoundResourceExpression(new("xamlg://snapshot/Child.xaml", root.Type, "Child.xaml", "Generated", null), new(2, 3))
                : new BoundConstantExpression(null, property.Type, default);
            var document = seed with { Root = root with
                { Assignments = shape == "empty" ? [] : [new BoundSetAssignment(member, value, default)] } };
            var expected = references(document, token);
            if (expected.Length != (shape == "positive" ? 1 : 0)) throw new InvalidOperationException("Unexpected resource snapshot");
            signature.Append(shape).Append(cancellable).Append(JsonSerializer.Serialize(expected.Select(r => new
                { r.Resource.Uri, r.Resource.LocalDocumentId, r.Resource.GeneratedNamespace, r.Span })));
            var suffix = shape + "-" + (cancellable ? "cancellable" : "default") + "-1000";
            measure("resource-snapshot-warm-" + suffix, 10, () =>
            {
                var checksum = 0;
                for (var i = 0; i < 1000; i++) checksum += references(document, token).Length;
                return checksum;
            });
            measure("resource-snapshot-cold-" + suffix, 3, () =>
            {
                var checksum = 0;
                for (var i = 0; i < 1000; i++)
                {
                    var fresh = document with { Root = document.Root! with { } };
                    checksum += references(fresh, token).Length;
                }
                return checksum;
            });
            if (!expected.Equals(references(document, token))) throw new InvalidOperationException("Warm snapshot identity changed");
            source.Cancel();
            try
            {
                _ = references(document, source.Token);
                throw new InvalidOperationException("Cancellation was ignored");
            }
            catch (OperationCanceledException) { signature.Append("cancelled"); }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())));
    }
}
