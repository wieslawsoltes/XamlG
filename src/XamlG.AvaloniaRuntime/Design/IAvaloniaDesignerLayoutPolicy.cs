using Avalonia.Controls;
using XamlG.Runtime.Design;

namespace XamlG.AvaloniaRuntime.Design;

public interface IAvaloniaDesignerLayoutPolicy
{
    IReadOnlyDictionary<string, string> GetPropertyEdits(Control control, XamlDesignRect before, XamlDesignRect after, XamlResizeHandle handle);
}
