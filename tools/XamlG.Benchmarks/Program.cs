using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlG.Frameworks.Avalonia;
using XamlG.Generator;
using XamlG.Roslyn;

var samples = args.Length > 0 ? int.Parse(args[0]) : 9;
var documentCount = args.Length > 1 ? int.Parse(args[1]) : 32;
var controlsPerDocument = args.Length > 2 ? int.Parse(args[2]) : 24;
if (samples < 3 || documentCount < 1 || controlsPerDocument < 1)
    throw new ArgumentException("Usage: XamlG.Benchmarks [samples >= 3] [documents >= 1] [controls per document >= 1]");

var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var source = CSharpSyntaxTree.ParseText("""
    namespace BenchmarkModel;
    public sealed class BenchmarkText
    {
        public string Value { get; set; } = "";
        public string ProvideValue() => Value;
    }
    """, parseOptions, "Model.cs");
var compilation = CSharpCompilation.Create("XamlG.Benchmark.Application", new[] { source }, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
var files = Enumerable.Range(0, documentCount).Select(index => (AdditionalText)new BenchmarkTextFile($"View{index}.axaml",
    "<StackPanel xmlns='https://github.com/avaloniaui' xmlns:b='clr-namespace:BenchmarkModel'>" +
    string.Concat(Enumerable.Range(0, controlsPerDocument).Select(child => $"<TextBlock Text='{{b:BenchmarkText Value=document-{index}-item-{child}}}' Margin='2'/>")) +
    "</StackPanel>")).ToArray();

GeneratorDriver CreateDriver() => CSharpGeneratorDriver.Create(new[] { new XamlIncrementalGenerator().AsSourceGenerator() },
    additionalTexts: files, parseOptions: parseOptions);

void Validate(GeneratorDriver driver)
{
    var result = driver.GetRunResult();
    var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != 0 || result.GeneratedTrees.Length != documentCount || result.Results.Any(item => item.Exception != null))
        throw new InvalidOperationException("Generator benchmark failed correctness validation: " + string.Join("\n", errors.AsEnumerable()));
}

// Validate actual generated C# before timing generation separately from application emission.
var initial = CreateDriver().RunGeneratorsAndUpdateCompilation(compilation, out var application, out var diagnostics);
Validate(initial);
using (var image = new MemoryStream())
{
    var emitted = application.Emit(image);
    if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
}

var measurements = new List<Measurement>();
measurements.Add(Measure("fresh-compilation-and-driver", () => CreateDriver().RunGenerators(
    CSharpCompilation.Create(compilation.AssemblyName!, new[] { source }, references, compilation.Options)), Validate));
measurements.Add(Measure("fresh-driver", () => CreateDriver().RunGenerators(compilation), Validate));
var unchanged = CreateDriver().RunGenerators(compilation);
measurements.Add(Measure("unchanged", () => unchanged = unchanged.RunGenerators(compilation), Validate));

var editDriver = CreateDriver().RunGenerators(compilation);
var editedFile = files[^1];
var revision = 0;
measurements.Add(Measure("one-xaml-edit", () =>
{
    var replacement = new BenchmarkTextFile(editedFile.Path, files[^1].GetText()!.ToString().Replace("Margin='2'", $"Margin='{++revision + 2}'"));
    editDriver = editDriver.ReplaceAdditionalText(editedFile, replacement).RunGenerators(compilation);
    editedFile = replacement;
    return editDriver;
}, Validate));

var csharpDriver = CreateDriver().RunGenerators(compilation);
measurements.Add(Measure("unrelated-csharp-edit", () =>
{
    var edit = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText($"internal class Unrelated{++revision} {{ }}", parseOptions));
    return csharpDriver = csharpDriver.RunGenerators(edit);
}, Validate));

var types = new RoslynTypeSystem(compilation, AvaloniaFrameworkProfile.Create().TypeSystem);
var candidates = new[] { "Avalonia.Controls.TextBlock", "Avalonia.Controls.StackPanel", "Avalonia.Controls.Button", "BenchmarkModel.BenchmarkText" }
    .Select(name => types.Find(name) ?? throw new InvalidOperationException("Missing benchmark type: " + name)).ToArray();
measurements.Add(Measure("provider-selection-10000", () =>
{
    var count = 0;
    for (var index = 0; index < 10000; index++)
        if (types.MarkupExtensionMethod(candidates[index % candidates.Length]) != null) count++;
    return count;
}, count => { if (count != 2500) throw new InvalidOperationException("Provider selection changed."); }));

Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    samples, documentCount, controlsPerDocument,
    allocationScope = "all process threads (includes concurrent document compilation)",
    correctness = "All generated documents compile; each measured invocation has no generator errors.",
    measurements
}, new JsonSerializerOptions { WriteIndented = true }));

Measurement Measure<T>(string name, Func<T> action, Action<T> validate)
{
    for (var index = 0; index < 3; index++) validate(action());
    var times = new double[samples];
    var allocations = new long[samples];
    for (var index = 0; index < samples; index++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        var result = action();
        times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[index] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        validate(result);
    }
    Array.Sort(times); Array.Sort(allocations);
    return new(name, times[samples / 2], times[0], times[^1], allocations[samples / 2]);
}

internal sealed record Measurement(string Scenario, double MedianMilliseconds, double MinimumMilliseconds, double MaximumMilliseconds, long MedianAllocatedBytes);
internal sealed class BenchmarkTextFile(string path, string text) : AdditionalText
{
    private readonly SourceText _text = SourceText.From(text);
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
}
