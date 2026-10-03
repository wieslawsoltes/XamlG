using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Tooling;

namespace XamlG.Tooling.Tests;

internal static class ToolingFixture
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    public static XamlCompilationSession Create() => new(CSharpCompilation.Create("ToolingTest",
        new[] { CSharpSyntaxTree.ParseText("namespace Model { public enum Alignment { Left, Center, Right } public class View { public string Text {get;set;} public bool Enabled {get;set;} public Alignment Alignment {get;set;} } }", path: "Model.cs") },
        References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
}
