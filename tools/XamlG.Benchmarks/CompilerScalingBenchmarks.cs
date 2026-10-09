using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;

internal static class CompilerScalingBenchmarks
{
    public static void Run(int samples)
    {
        if (samples < 3) throw new ArgumentOutOfRangeException(nameof(samples), "Use at least three samples.");
        var walk = typeof(CSharpEmitter).Assembly.GetType("XamlG.CSharp.BoundTraversal")!
            .GetMethod("Objects", [typeof(BoundObject), typeof(bool)])!
            .CreateDelegate<Func<BoundObject, bool, IEnumerable<BoundObject>>>();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Scaling",
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "namespace Perf { public class Node { public Node Child { get; set; } public string Text { get; set; } } }")],
            references, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var original = new XamlCompiler().Bind(XamlSyntaxTree.Parse("<Node xmlns='clr-namespace:Perf' Text='end'/>", "Tree.xaml"), compilation);
        if (!original.Success) throw new InvalidOperationException(string.Join("\n", original.Diagnostics));
        var leaf = original.Root!;
        var property = (IPropertySymbol)leaf.Type.GetMembers("Child").Single();
        var member = new BoundMember(property.Name, BoundMemberKind.Property, property, property.Type,
            property.GetMethod, property.SetMethod, leaf.Syntax.Span);
        var emitter = new CSharpEmitter();
        var rows = new List<object>();
        using var process = Process.GetCurrentProcess();

        foreach (var shape in new[] { "deep", "wide" })
        foreach (var size in new[] { 16, 64, 128, 256, 512, 1024 })
        {
            var node = leaf with { IsRoot = false, Key = "n" + size };
            if (shape == "deep")
                for (var i = size - 1; i >= 0; i--)
                    node = leaf with { IsRoot = i == 0, Key = "n" + i,
                        Assignments = [new BoundSetAssignment(member, new BoundObjectExpression(node), leaf.Syntax.Span)] };
            else
                node = leaf with { Assignments = Enumerable.Range(0, size).Select(index => (BoundAssignment)new BoundSetAssignment(member,
                    new BoundObjectExpression(leaf with { IsRoot = false, Key = "n" + index }), leaf.Syntax.Span)).ToImmutableArray() };
            var document = original with { Root = node };
            Measure("walk", shape, size, 100, () =>
            {
                var count = 0;
                foreach (var item in walk(node, true)) count++;
                return count;
            }, count =>
            {
                if (count != size + 1) throw new InvalidOperationException("Incorrect object traversal count.");
            });
            if (size <= 512)
            {
                var initial = Generate();
                Measure("emit", shape, size, 5, Generate, source =>
                {
                    if (source != initial) throw new InvalidOperationException("Emission is not deterministic.");
                }, initial);
                string Generate()
                {
                    var result = emitter.Emit(document);
                    if (!result.Success) throw new InvalidOperationException("Emission failed.");
                    return result.Source;
                }
            }
        }

        foreach (var shape in new[] { "deep", "wide" })
        foreach (var size in new[] { 32, 128, 512, 2048 })
        {
            if (shape == "deep" && size > 512) continue;
            var source = shape == "wide" ? "<Root>" + string.Concat(Enumerable.Repeat("<Node/>", size)) + "</Root>" :
                string.Concat(Enumerable.Repeat("<Node>", size)) + "<Leaf/>" + string.Concat(Enumerable.Repeat("</Node>", size));
            var options = new XamlParseOptions(MaximumDepth: size + 2);
            var tree = XamlSyntaxTree.Parse(source, options: options);
            if (tree.HasErrors) throw new InvalidOperationException("Lookup input failed to parse.");
            var spans = tree.Root!.DescendantsAndSelf().Select(element => element.Span).ToArray();
            int Locate(XamlSyntaxTree input)
            {
                var count = 0;
                foreach (var span in spans)
                    if (input.FindElement(span.Start + 1)?.Span == span) count++;
                return count;
            }
            void Validate(int count)
            {
                if (count != spans.Length) throw new InvalidOperationException("Source element lookup changed.");
            }
            Measure("locate-warm", shape, size, 1, () => Locate(tree), Validate);
            Measure("parse-and-locate-cold", shape, size, 1, () => Locate(XamlSyntaxTree.Parse(source, options: options)), Validate);
        }

        foreach (var shape in new[] { "plain", "unterminated", "distant-semicolon" })
        foreach (var size in new[] { 1024, 4096, 16384, 65536 })
        {
            var raw = new string(shape == "plain" ? 'a' : '&', size) + (shape == "distant-semicolon" ? ";" : "");
            var source = "<Root Value='" + raw + "'/>";
            Measure("parse", shape, size, 5, () => XamlSyntaxTree.Parse(source), tree =>
            {
                if (tree.Root!.Attributes[0].Value != raw || tree.Diagnostics.Length != (shape == "plain" ? 0 : 256))
                    throw new InvalidOperationException("XML recovery changed.");
            });
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scope = "Synthetic depth/width and malformed-XML scaling; emission excludes binding and generated-C# compilation. " +
                    "These are diagnostic measurements, not XamlX acceptance timings. All repetitions retain results and validate after timing.",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processorCount = Environment.ProcessorCount,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            samples,
            assemblies = new[] { typeof(XamlSyntaxTree).Assembly, typeof(XamlCompiler).Assembly, typeof(CSharpEmitter).Assembly }
                .Select(assembly => new { name = assembly.GetName().Name, sha256 = Hash(File.ReadAllBytes(assembly.Location)) }),
            rows
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure<T>(string phase, string shape, int size, int repetitions, Func<T> action, Action<T> validate, string? source = null)
        {
            for (var i = 0; i < 3; i++) validate(action());
            var results = new T[repetitions];
            for (var iteration = 0; iteration < samples; iteration++)
            {
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < repetitions; i++) results[i] = action();
                var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                foreach (var result in results) validate(result);
                rows.Add(new { phase, shape, size, iteration, repetitions, milliseconds, cpuMilliseconds, bytes,
                    sourceBytes = source == null ? (int?)null : Encoding.UTF8.GetByteCount(source),
                    sourceSha256 = source == null ? null : Hash(Encoding.UTF8.GetBytes(source)) });
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
