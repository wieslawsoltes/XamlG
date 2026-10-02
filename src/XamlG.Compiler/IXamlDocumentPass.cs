namespace XamlG.Compiler;
public interface IXamlDocumentPass
{
    string Name { get; }
    BoundDocument Run(BoundDocument document, BindingContext context);
}
