namespace XamlG.Playground;

/// <summary>One typed render snapshot for a file-move preview. Keeping the path/error inputs
/// together prevents a Razor string literal from masquerading as a bound field name.</summary>
public sealed record FileMoveDialogState(string Source, string Destination, string? Error);
