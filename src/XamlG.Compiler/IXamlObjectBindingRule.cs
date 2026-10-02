namespace XamlG.Compiler;
/// <summary>Order-independent object metadata, established before attributes or children are bound.</summary>
public interface IXamlObjectBindingRule
{
    void Initialize(BindingContext context, ObjectBindingBuilder target);
    void Complete(BindingContext context, ObjectBindingBuilder target);
}
