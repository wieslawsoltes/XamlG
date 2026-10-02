namespace XamlG.Runtime;
public interface IXamlRootObjectProvider
{
    object? RootObject { get; }
    object? IntermediateRootObject { get; }
}
