using XamlG.AvaloniaRuntime;

namespace XamlG.Playground.Isolation;

public sealed record SandboxRunResult(AvaloniaVisualNode Tree, long Revision, IReadOnlyList<string> Warnings);
