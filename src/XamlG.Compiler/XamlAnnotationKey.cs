namespace XamlG.Compiler;
/// <summary>An identity-keyed typed slot; equal display names do not alias unrelated extensions.</summary>
public sealed class XamlAnnotationKey<T>
{
    public XamlAnnotationKey(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));
    public string Name { get; }
}
