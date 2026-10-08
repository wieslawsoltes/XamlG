using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

internal sealed class CSharpTestProject
{
    internal static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    internal CSharpCompilation Compilation { get; }
    internal CSharpLanguageService Service { get; }

    internal CSharpTestProject(params (string Path, string Text)[] files) : this(new CSharpCompilationSettings(), files) { }

    internal CSharpTestProject(CSharpCompilationSettings settings, params (string Path, string Text)[] files)
    {
        Compilation = CSharpCompilation.Create("InspectionTests", files.Select(file => CSharpSyntaxTree.ParseText(file.Text,
            settings.CreateParseOptions(), file.Path)), References, settings.CreateCompilationOptions());
        Service = new(Compilation, files.Where(file => !file.Path.EndsWith(".g.cs", StringComparison.Ordinal)).Select(file => file.Path));
    }

    internal void AssertCompiles() => Assert.Empty(Compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    internal static string Apply(string source, XamlCodeAction action) => SourceText.From(source).WithChanges(action.Changes.Select(change =>
        new TextChange(new(change.Span.Start, change.Span.Length), change.NewText))).ToString();

    internal object? Run(string type = "Probe", string method = "Run")
    {
        using var stream = new MemoryStream();
        var emission = Compilation.Emit(stream);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("CSharp behavior check", isCollectible: true);
        try { return context.LoadFromStream(stream).GetType(type)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null); }
        finally { context.Unload(); }
    }
}
