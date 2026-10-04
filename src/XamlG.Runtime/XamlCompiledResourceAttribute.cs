namespace XamlG.Runtime;

/// <summary>Metadata-only discovery of a statically compiled resource factory. Never invokes the factory.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XamlCompiledResourceAttribute : Attribute
{
    public XamlCompiledResourceAttribute(string uri, Type factoryType, string methodName)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        FactoryType = factoryType ?? throw new ArgumentNullException(nameof(factoryType));
        MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
    }
    public string Uri { get; }
    public Type FactoryType { get; }
    public string MethodName { get; }
}
