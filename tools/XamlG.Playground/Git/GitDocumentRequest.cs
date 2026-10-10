namespace XamlG.Playground.Git;

public sealed record GitDocumentRequest(string Id, string Title);
public sealed record GitSourceRequest(string Path, string Text);
