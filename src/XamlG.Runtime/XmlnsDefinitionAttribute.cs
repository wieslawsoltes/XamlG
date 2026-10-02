namespace XamlG.Runtime;
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class XmlnsDefinitionAttribute : Attribute
{
    public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) { XmlNamespace = xmlNamespace; ClrNamespace = clrNamespace; }
    public string XmlNamespace { get; }
    public string ClrNamespace { get; }
    public string? AssemblyName { get; set; }
}
