using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
namespace XamlG.Tests;
internal static class CompilationFactory
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    public static CSharpCompilation Create(string source) => CSharpCompilation.Create("TestAssembly", new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)) }, References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
