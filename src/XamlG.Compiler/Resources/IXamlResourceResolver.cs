namespace XamlG.Compiler.Resources;

/// <summary>Pure metadata lookup. Implementations must not open files, fetch URLs or execute factories.</summary>
public interface IXamlResourceResolver
{
    XamlResourceLookup Resolve(string? baseUri, string source);
}
