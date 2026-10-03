using Microsoft.CodeAnalysis;

namespace XamlG.Playground.Isolation;

/// <summary>Emits an assembly as data in the editor. No generated application code is loaded into the editor by this path.</summary>
public static class SandboxPayloadBuilder
{
    public const int MaximumAssemblyBytes = 8 * 1024 * 1024;
    public static SandboxRunRequest Create(BrowserCompilation compilation)
    {
        if (!compilation.Success) throw new InvalidOperationException("The compilation has errors.");
        using var stream = new MemoryStream();
        var emitted = compilation.Compilation.Emit(stream);
        if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        if (stream.Length > MaximumAssemblyBytes) throw new InvalidOperationException("The isolated preview assembly exceeds the 8 MiB limit.");
        var output = compilation.Analysis.Output;
        return new(Convert.ToBase64String(stream.ToArray()), output.FactoryTypeName, output.BuildMethodName, output.PopulateMethodName);
    }
}
