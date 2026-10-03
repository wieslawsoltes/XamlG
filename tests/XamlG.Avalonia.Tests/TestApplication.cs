using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Simple;

[assembly: AvaloniaTestApplication(typeof(XamlG.Avalonia.Tests.TestApplication))]

namespace XamlG.Avalonia.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
