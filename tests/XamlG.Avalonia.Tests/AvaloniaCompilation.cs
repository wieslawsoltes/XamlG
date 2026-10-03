using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Frameworks.Avalonia;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Avalonia.Tests;

internal static class AvaloniaCompilation
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();

    public static object Build(string xaml)
    {
        var compilation = CSharpCompilation.Create("XamlG.AvaloniaTest." + Guid.NewGuid().ToString("N"), references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var bound = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "Test.axaml"), compilation, AvaloniaFrameworkProfile.Create());
        Assert.True(bound.Success, string.Join("\n", bound.Diagnostics));
        var emitted = new CSharpEmitter().Emit(bound);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(emitted.Source, new CSharpParseOptions(LanguageVersion.Preview)));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) + "\n" + emitted.Source);
        return Assembly.Load(stream.ToArray()).GetType(emitted.FactoryTypeName)!.GetMethod(emitted.BuildMethodName!)!.Invoke(null, new object?[] { null })!;
    }
}
