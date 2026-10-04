namespace XamlG.Compiler.Resources;

public sealed record XamlResourceLookup(XamlResourceDescriptor? Resource, string? Error)
{
    public bool Success => Resource != null && Error == null;
}
