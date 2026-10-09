namespace XamlG.CSharp;

internal sealed record SharedGeneratedSource(string TypeName, string Source)
{
    // Multiple independently published members can contribute to one partial type.
    public string Identity { get; init; } = TypeName;
}
