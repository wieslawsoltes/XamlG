namespace XamlG.Tooling.Formatting;

public sealed record XamlFormattingOptions
{
    public int TabSize { get; init; } = 4;
    public bool InsertSpaces { get; init; } = true;
    public bool InsertFinalNewline { get; init; }
    public bool SpaceBeforeSelfClosingSlash { get; init; } = true;
    public bool SplitAttributes { get; init; }
    public int MaximumLineLength { get; init; } = 120;
}
