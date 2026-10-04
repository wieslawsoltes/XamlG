using Microsoft.JSInterop;

namespace XamlG.Playground.Editing;

/// <summary>One editor handle. The module is borrowed from the application scope, not owned by this handle.</summary>
internal sealed record EditorJsHandle(IJSObjectReference Module, int Id);
