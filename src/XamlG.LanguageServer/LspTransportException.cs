namespace XamlG.LanguageServer;

/// <summary>A frame write may have been partial. The connection is permanently unusable.</summary>
public sealed class LspTransportException(string message, Exception innerException) : IOException(message, innerException);
