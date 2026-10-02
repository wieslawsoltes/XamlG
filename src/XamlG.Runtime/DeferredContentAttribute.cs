namespace XamlG.Runtime;
[AttributeUsage(AttributeTargets.Property)]
public sealed class DeferredContentAttribute : Attribute
{
    public Type? Type { get; set; }
}
