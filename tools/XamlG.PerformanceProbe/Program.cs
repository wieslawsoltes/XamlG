using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XamlG.Runtime;
using XamlG.Syntax;

if (args.Length != 1) throw new ArgumentException("Usage: XamlG.PerformanceProbe <output.json>");
var measurements = new List<Measurement>();
var pipelineSignature = PipelineProbe.Run(Measure);
var emissionSignature = EmissionScalingProbe.Run(Measure);
var analysisSignature = EmissionAnalysisProbe.Run(Measure);
var namespaceSignature = NamespaceEmissionProbe.Run(Measure);
var generatedSources = NamedFieldProbe.Run(Measure);
var markupAssignments = MarkupAssignmentProbe.Run(Measure);
var semanticInputs = new List<string>
{
    "<Root/>", "<Root></Root>", "<Root A='1' A='2'/>", "<Root A='broken<Child/></Root>",
    "<Root><Child></Root>", "<Root A='one\t&amp;\r\ntwo'/>", "<!--a--><Root><![CDATA[x\r\ny]]></Root>",
    "<!DOCTYPE Root [<!ENTITY e 'forbidden'>]><Root>&e;</Root>", "<Root/><Other/>",
    "<Root A='&#x1F600;' B='&quot;' C='&#13;'/>"
};
foreach (var count in new[] { 100, 1000, 10000 })
{
    var repeated = "<Root>" + string.Concat(Enumerable.Repeat("<Item Name='name' Text='value' Width='12'/>", count)) + "</Root>";
    var unique = "<Root>" + string.Concat(Enumerable.Range(0, count).Select(i => "<Node" + i + " A='1'/>")) + "</Root>";
    semanticInputs.Add(repeated);
    semanticInputs.Add(unique);
    var operations = Math.Max(2, 10000 / count);
    Measure("xml-repeated-" + count, operations, () => XamlSyntaxTree.Parse(repeated).Root!.Children.Length);
    Measure("xml-unique-" + count, operations, () => XamlSyntaxTree.Parse(unique).Root!.Children.Length);
}
var manyAttributes = "<Root " + string.Join(" ", Enumerable.Range(0, 1000).Select(i => "A" + i + "='v'")) + " A0='duplicate'/>";
semanticInputs.Add(manyAttributes);
Measure("xml-attributes-1000", 10, () => XamlSyntaxTree.Parse(manyAttributes).Diagnostics.Length);

const string markupText = "{Binding Path=Customer.Name, Mode=TwoWay, FallbackValue={StaticResource Missing}}";
var markupSource = "prefix" + markupText + "suffix";
var markupSpan = new TextSpan(6, markupText.Length);
Measure("markup-identity", 10000, () => MarkupExtensionParser.ParseAtSource(markupText, markupSpan, markupSource, static _ => { })!.Arguments.Length);
const string encodedSource = "<Root Value='{Binding Path=&quot;A&amp;B&quot;, Mode=TwoWay}'/>";
var attribute = XamlSyntaxTree.Parse(encodedSource).Root!.Attributes[0];
Measure("markup-entities", 10000, () => MarkupExtensionParser.ParseAtSource(attribute.Value, attribute.ValueSpan, encodedSource, static _ => { })!.Arguments.Length);

var encodedRecords = new StringBuilder();
for (var i = 0; i < 1000; i++)
{
    var record = i.ToString(CultureInfo.InvariantCulture) + ":12:4:node4:hash2:4:Name5:Value4:Text4:Text";
    encodedRecords.Append(record.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(record);
}
var records = encodedRecords.ToString();
Measure("metadata-frame-1000", 20, () => { GC.KeepAlive(XamlSourceInfoTable.FromEncoded("view.xaml", 1, records)); return 1000; });
Measure("metadata-decode-1000", 10, () =>
{
    var table = XamlSourceInfoTable.FromEncoded("view.xaml", 1, records);
    var checksum = 0;
    for (var i = 0; i < 1000; i++) checksum += table[i].Start + table[i].Declarations.Count;
    return checksum;
});

// Correctness work is outside all measurements. Hash complete typed nodes and
// diagnostics, including source spelling/ranges, across deterministic mutations.
const string seed = "<Root xmlns:p='urn:test' A='&amp;'><p:Item Name='n' Value='{Binding X, Mode=TwoWay}'/><Text>hello</Text></Root>";
var random = new Random(194731);
var insertions = new[] { "<", ">", "'", "\"", "&", "\r\n", " ", "/>", "</Root>", "&#x1F600;", "\u00a0" };
for (var i = 0; i < 500; i++)
{
    var position = random.Next(seed.Length + 1);
    var remove = Math.Min(random.Next(4), seed.Length - position);
    semanticInputs.Add(seed.Remove(position, remove).Insert(position, insertions[random.Next(insertions.Length)]));
}
using var snapshot = new MemoryStream();
using (var writer = new BinaryWriter(snapshot, Encoding.UTF8, leaveOpen: true))
{
    foreach (var source in semanticInputs)
    {
        var tree = XamlSyntaxTree.Parse(source, "probe.xaml");
        writer.Write(source);
        writer.Write(JsonSerializer.Serialize(tree.Diagnostics));
        foreach (var node in tree.Nodes) WriteNode(writer, node);
    }
    foreach (var text in new[] { markupText, "{Binding}", "{Binding X='A' Y='B'}", "{Binding X=1, X=2}", "{Binding X='bad}", "{Binding X", "{}literal" })
    {
        var diagnostics = new List<XamlDiagnostic>();
        writer.Write(JsonSerializer.Serialize(MarkupExtensionParser.ParseAtSource(text, new(6, text.Length), "prefix" + text, diagnostics.Add)));
        writer.Write(JsonSerializer.Serialize(diagnostics));
    }
    writer.Write(JsonSerializer.Serialize(MarkupExtensionParser.ParseAtSource(attribute.Value, attribute.ValueSpan, encodedSource, static _ => { })));
    var metadata = XamlSourceInfoTable.FromEncoded("view.xaml", 1, records);
    for (var i = 0; i < 1000; i++) writer.Write(JsonSerializer.Serialize(metadata[i]));
    writer.Write(pipelineSignature);
    writer.Write(emissionSignature);
    writer.Write(analysisSignature);
    writer.Write(namespaceSignature);
    foreach (var source in generatedSources) writer.Write(source);
    foreach (var markup in markupAssignments) writer.Write(markup.SemanticSignature);
}
var result = new
{
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    processors = Environment.ProcessorCount,
    semantic_inputs = semanticInputs.Count,
    semantic_sha256 = Convert.ToHexString(SHA256.HashData(snapshot.ToArray())),
    measurements,
    markup_assignments = markupAssignments
};
File.WriteAllText(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");

void Measure(string name, int operations, Func<int> operation)
{
    var checksum = 0;
    // Tiering is explicitly disabled by the comparison harness. These calls warm
    // code and data; restores, startup, input generation and hashing are not timed.
    for (var i = 0; i < 3; i++) checksum ^= operation();
    var times = new double[5];
    var allocations = new double[5];
    for (var sample = 0; sample < times.Length; sample++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < operations; i++) checksum ^= operation();
        var elapsed = Stopwatch.GetTimestamp() - start;
        allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)operations;
        times[sample] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / operations;
    }
    GC.KeepAlive(checksum);
    measurements.Add(new(name, operations, times, allocations));
}

static void WriteNode(BinaryWriter writer, XamlSyntaxNode node)
{
    writer.Write(node.GetType().Name);
    writer.Write(JsonSerializer.Serialize(node, node.GetType()));
    if (node is XamlElementSyntax element)
        foreach (var child in element.Children) WriteNode(writer, child);
}

internal sealed record Measurement(string Name, int Operations, double[] Nanoseconds, double[] AllocatedBytes);
