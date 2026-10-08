using XamlG.Tooling;

namespace XamlG.Playground;

public partial class App
{
    private async Task ApplyDesignerPropertyAsync()
    {
        if (_selectedElement == null || _busy) return;
        try
        {
            await CommitDesignerTransactionAsync(XamlDesignerEdits.SetProperty(SelectedDesignerSyntax, _selectedElement, _propertyName, _propertyValue));
        }
        catch (Exception error) { Report(error); }
    }
}
