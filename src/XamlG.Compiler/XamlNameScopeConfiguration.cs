namespace XamlG.Compiler;

public sealed record XamlNameScopeConfiguration(string ConcreteMetadataName, string ContractMetadataName)
{
    public string RegisterMethod { get; init; } = "Register";
    public string CompleteMethod { get; init; } = "Complete";
    public XamlMethodReference? Attach { get; init; }
}
