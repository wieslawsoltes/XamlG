namespace XamlG.Runtime;
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, Inherited = true)]
public sealed class ContentAttribute : Attribute
{
    public ContentAttribute() { }
    public ContentAttribute(string name) => Name = name;
    public string? Name { get; }
}
