namespace XamlG.LanguageServer;

public sealed class LspRequestException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
