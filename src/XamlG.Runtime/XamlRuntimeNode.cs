namespace XamlG.Runtime;
/// <summary>One generated object and its logical construction parent. Framework visual trees remain a separate adapter concern.</summary>
public sealed record XamlRuntimeNode(string Key, object Instance, string? ParentKey);
