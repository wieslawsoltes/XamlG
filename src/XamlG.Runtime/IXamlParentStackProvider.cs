namespace XamlG.Runtime;
public interface IXamlParentStackProvider
{
    IEnumerable<object> Parents { get; }
}
