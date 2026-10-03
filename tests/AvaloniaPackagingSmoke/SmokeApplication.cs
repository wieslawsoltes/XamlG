using Avalonia;
using Avalonia.Themes.Simple;

namespace AvaloniaPackagingSmoke;

public sealed class SmokeApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
