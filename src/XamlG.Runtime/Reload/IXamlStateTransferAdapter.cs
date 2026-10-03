namespace XamlG.Runtime.Reload;

/// <summary>Captures application state without mutating the displayed graph. Prepare runs before any candidate mutation.</summary>
public interface IXamlStateTransferAdapter
{
    XamlStateTransferOperation? Prepare(XamlStateTransferContext context);
}
