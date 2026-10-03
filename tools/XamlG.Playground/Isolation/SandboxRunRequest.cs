namespace XamlG.Playground.Isolation;

public sealed record SandboxRunRequest(string AssemblyBase64, string FactoryType, string? BuildMethod, string PopulateMethod);
