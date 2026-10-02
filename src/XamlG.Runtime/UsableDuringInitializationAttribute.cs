namespace XamlG.Runtime;
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class UsableDuringInitializationAttribute : Attribute
{
    public UsableDuringInitializationAttribute(bool usable) => Usable = usable;
    public bool Usable { get; }
}
