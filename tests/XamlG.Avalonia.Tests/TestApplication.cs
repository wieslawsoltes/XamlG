using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Simple;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(XamlG.Avalonia.Tests.TestApplication))]
// Per-test headless setup resets process-wide Dispatcher and locator state.
// Ordinary compiler facts in this assembly also touch Avalonia types and may
// construct controls; their collections must not race the headless reset queue.
// Keep PerTest isolation and all test cases; other test assemblies stay parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace XamlG.Avalonia.Tests;

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
