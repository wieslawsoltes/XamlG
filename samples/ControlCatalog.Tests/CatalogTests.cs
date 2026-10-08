using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ControlCatalog.Models;
using ControlCatalog.Pages;
using ControlCatalog.Validation;
using XamlG.Runtime;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ControlCatalog.Tests.CatalogTestApplication))]

namespace ControlCatalog.Tests;

public static class CatalogTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class CatalogTests
{
    [AvaloniaFact]
    public void FluidNavigationDefaultsRemainStyleable()
    {
        var control = new FluidNavBar();
        Assert.Empty(control.Items);
        Assert.Equal(Colors.White, control.BarColor);
        Assert.Equal(Colors.White, control.ButtonColor);
        Assert.Equal(Colors.Black, control.ActiveIconColor);
        Assert.Equal(Color.FromArgb(140, 120, 120, 120), control.InactiveIconColor);
        using (control.SetValue(FluidNavBar.BarColorProperty, Colors.Red, BindingPriority.Style))
            Assert.Equal(Colors.Red, control.BarColor);
        Assert.Equal(Colors.White, control.BarColor);
    }

    [AvaloniaTheory]
    [InlineData(CatalogTheme.Simple)]
    [InlineData(CatalogTheme.Fluent)]
    public void EveryImportedXamlComponentConstructs(CatalogTheme theme)
    {
        App.SetCatalogThemes(theme);
        var assembly = typeof(App).Assembly;
        var classes = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "components.json")))!;
        Assert.Equal(218, classes.Length);
        var exports = assembly.GetCustomAttributes<XamlCompiledResourceAttribute>().ToArray();
        var errors = new List<string>();
        var progress = Path.Combine(Environment.GetEnvironmentVariable("XAMLG_CATALOG_RESULTS") ?? AppContext.BaseDirectory,
            "components-" + theme + ".log");
        Directory.CreateDirectory(Path.GetDirectoryName(progress)!);
        File.WriteAllText(progress, "");
        foreach (var name in classes)
        {
            // The real Application has already been initialized by the headless host.
            if (name == typeof(App).FullName) continue;
            try
            {
                File.AppendAllText(progress, name + "\n");
                var type = assembly.GetType(name, throwOnError: true)!;
                var factory = exports.Select(export => export.FactoryType.GetMethod(export.MethodName)!)
                    .Single(method => method.ReturnType == type);
                var value = factory.Invoke(null, new object?[] { null });
                Assert.IsType(type, value);
                Assert.True(XamlRuntimeSession.TryGet(value!, out var session), name + " has no generated initializer session.");
                session!.Dispose();
            }
            catch (Exception error) { errors.Add(name + ": " + error); }
        }
        Assert.True(errors.Count == 0, string.Join("\n\n", errors));
    }

    [AvaloniaTheory]
    [InlineData(CatalogTheme.Simple, false, false)]
    [InlineData(CatalogTheme.Simple, true, false)]
    [InlineData(CatalogTheme.Fluent, false, false)]
    [InlineData(CatalogTheme.Fluent, true, false)]
    [InlineData(CatalogTheme.Fluent, false, true)]
    [InlineData(CatalogTheme.Fluent, true, true)]
    public async Task EveryPageSectionAndGallerySampleRenders(CatalogTheme theme, bool dark, bool compact)
    {
        AssertCompiledWithXamlG(typeof(App));
        AssertCompiledWithXamlG(typeof(SimpleTheme));
        AssertCompiledWithXamlG(typeof(FluentTheme));
        App.SetCatalogThemes(theme);
        var shell = CatalogCases.CreateShell();
        ((FluentTheme)Application.Current!.Resources["FluentTheme"]!).DensityStyle =
            compact ? DensityStyle.Compact : DensityStyle.Normal;
        var cases = CatalogCases.All(shell);
        Assert.Equal(76, cases.Count(item => item.Kind == "page"));
        Assert.Contains(cases, item => item.Kind == "section");
        Assert.Contains(cases, item => item.Kind == "sample");
        Assert.Equal(102, cases.Count(item => item.Kind == "gallery"));
        var id = $"{theme}-{(dark ? "Dark" : "Light")}-{(compact ? "Compact" : "Normal")}";
        var output = Path.Combine(Environment.GetEnvironmentVariable("XAMLG_CATALOG_RESULTS") ??
            Path.Combine(AppContext.BaseDirectory, "test-results"), id);
        Directory.CreateDirectory(output);
        var results = new List<object>();
        var errors = new List<string>();
        var navigation = CatalogCases.Navigation(shell);
        navigation.PageTransition = null;
        var window = new Window { Width = 1280, Height = 800, DataContext = shell.DataContext,
            Content = new PageNavigationHost { Page = shell } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            foreach (var item in cases)
            {
                try
                {
                    var control = item.Create();
                    var page = control as Page ?? new ContentPage { Content = control, Header = item.Name };
                    await navigation.ReplaceAsync(page, null);
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                    Dispatcher.UIThread.RunJobs();
                    CatalogValidation.ApplyVariant(dark, compact);
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    Assert.True(frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0);
                    Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, item.Name + " has no layout.");
                    var visuals = control.GetVisualDescendants().Count();
                    Assert.True(visuals > 0, item.Name + " has no realized visual content.");
                    CatalogValidation.VerifyConfiguration(shell, control, theme, dark, compact);
                    CatalogValidation.VerifyBindings(control);
                    var file = string.Concat(item.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".png";
                    frame.Save(Path.Combine(output, file), new PngBitmapEncoderOptions());
                    results.Add(new { item.Name, item.Kind, Visuals = visuals, Status = "passed" });
                }
                catch (Exception error)
                {
                    errors.Add(item.Name + ": " + error);
                    results.Add(new { item.Name, item.Kind, Status = "failed", Error = error.ToString() });
                }
            }
        }
        finally
        {
            window.Close();
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(errors.Count == 0, string.Join("\n\n", errors));
    }

    private static void AssertCompiledWithXamlG(Type type)
    {
        Assert.Contains(type.Assembly.GetCustomAttributesData(), attribute =>
            attribute.AttributeType.FullName == "XamlG.Runtime.XamlCompiledResourceAttribute");
        Assert.Null(type.Assembly.GetType("CompiledAvaloniaXaml.!XamlLoader"));
    }
}
