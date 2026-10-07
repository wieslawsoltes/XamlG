using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp.Resources;
using XamlG.Frameworks.Avalonia;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

internal sealed class ResourceProjectFixture
{
    public const string Namespace = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(XamlCompiledResourceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    public ResourceProjectFixture(IEnumerable<(string Path, string Source)> documents, string? assemblyName = null, IEnumerable<MetadataReference>? references = null, string? sourceCode = null, bool createSourceInfo = false)
    {
        Compilation = CSharpCompilation.Create(assemblyName ?? "ResourceTest_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: sourceCode == null ? null : new[] { CSharpSyntaxTree.ParseText(sourceCode, new CSharpParseOptions(LanguageVersion.Preview), "Code.cs") }, references: References.Concat(references ?? Array.Empty<MetadataReference>()),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Result = new XamlProjectCompiler().Compile(documents.Select(d => new XamlProjectDocument(XamlSyntaxTree.Parse(d.Source, d.Path), d.Path)), Compilation, AvaloniaFrameworkProfile.Create(createSourceInfo: createSourceInfo));
    }
    public CSharpCompilation Compilation { get; }
    public XamlProjectCompilation Result { get; }
    public byte[] Emit()
    {
        Assert.True(Result.Success, string.Join("\n", Result.Documents.SelectMany(d => d.Output.Diagnostics.Select(e => d.Input.LogicalPath + ": " + e))));
        var generated = Result.Documents.Select(d => CSharpSyntaxTree.ParseText(d.Output.Source, new CSharpParseOptions(LanguageVersion.Preview), d.Output.HintName));
        using var output = new MemoryStream(); var emitted = Compilation.AddSyntaxTrees(generated).Emit(output);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + string.Join("\n", Result.Documents.Select(d => d.Output.Source)));
        return output.ToArray();
    }
    public static Assembly Load(byte[] image)
    {
        using var stream = new MemoryStream(image);
        return AssemblyLoadContext.Default.LoadFromStream(stream);
    }
    public object Build(string path, IServiceProvider? services = null)
    {
        var assembly = Load(Emit());
        var output = Result.Documents.Single(d => d.Input.LogicalPath == path).Output;
        return assembly.GetType(output.FactoryMetadataName)!.GetMethod(output.BuildMethodName!)!.Invoke(null, new object?[] { services })!;
    }
    public static string Dictionary(string content) => "<ResourceDictionary " + Namespace + ">" + content + "</ResourceDictionary>";
    public static string Include(string source, string kind = "ResourceInclude") =>
        "<ResourceDictionary.MergedDictionaries><" + kind + " Source='" + source + "'/></ResourceDictionary.MergedDictionaries>";
}
