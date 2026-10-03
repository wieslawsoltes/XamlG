using Avalonia;
using Avalonia.Themes.Simple;

namespace XamlG.Playground;

public sealed class PreviewApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
