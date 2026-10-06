using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace XamlG.Workspaces.Tests;

public sealed class EmissionInputsTests
{
    [Fact]
    public void EvaluatedResourcesRetainLogicalNamesVisibilityAndBinaryContents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xamlg-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = new byte[] { 0, 1, 127, 128, 255, 0 };
            File.WriteAllBytes(Path.Combine(directory, "Binary Asset.bin"), bytes);
            var inputs = XamlEmissionInputs.FromCommandLine(new[] { "/target:library", "/out:Assets.dll", "/resource:\"Binary Asset.bin\",Exact.Logical.Name,private" }, directory);
            Assert.Single(inputs.ManagedResources);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("Assets_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText("public class Marker { }") }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var result = inputs.Emit(compilation, image);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var assembly = Assembly.Load(image.ToArray());
            Assert.Equal("Exact.Logical.Name", Assert.Single(assembly.GetManifestResourceNames()));
            using var resource = assembly.GetManifestResourceStream("Exact.Logical.Name")!;
            using var contents = new MemoryStream(); resource.CopyTo(contents);
            Assert.Equal(bytes, contents.ToArray());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public async Task ResourcePreparationRequiresExplicitTrust()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => XamlMSBuildEmissionCollector.CollectAsync("Untrusted.csproj", new()));
    }
    [Fact]
    public void InvalidResourceArgumentsAreRejectedBeforeEmission()
    {
        Assert.Throws<InvalidDataException>(() => XamlEmissionInputs.FromCommandLine(new[] { "/resource:" }, Path.GetTempPath()));
    }
}
