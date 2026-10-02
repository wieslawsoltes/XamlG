namespace XamlG.Runtime;
public interface IXamlProvideValueTarget
{
    object? TargetObject { get; }
    object? TargetProperty { get; }
}
