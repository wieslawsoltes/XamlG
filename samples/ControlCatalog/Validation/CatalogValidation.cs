using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ControlCatalog.Models;
using ControlCatalog.Pages;
using XamlG.Runtime;

namespace ControlCatalog.Validation;

public sealed record CatalogValidationResult(string Theme, string Variant, string Density, string Name,
    string Kind, int Visuals, string? Error);

[JsonSerializable(typeof(List<CatalogValidationResult>))]
internal partial class CatalogValidationJsonContext : JsonSerializerContext;

/// <summary>Opt-in validation shared by the real desktop and WebAssembly hosts.</summary>
public static class CatalogValidation
{
    public static async Task<List<CatalogValidationResult>> RunAsync(Action<Control> show)
    {
        if (!XamlRuntimeSession.TryGet(Application.Current!, out _) ||
            !XamlRuntimeSession.TryGet(Application.Current!.Resources["SimpleTheme"]!, out _) ||
            !XamlRuntimeSession.TryGet(Application.Current.Resources["FluentTheme"]!, out _))
            throw new InvalidOperationException("The application and both themes must have XamlG initializer sessions.");
        var results = new List<CatalogValidationResult>();
        foreach (var theme in new[] { CatalogTheme.Simple, CatalogTheme.Fluent })
        foreach (var dark in new[] { false, true })
        foreach (var compact in theme == CatalogTheme.Fluent ? new[] { false, true } : new[] { false })
        {
            App.SetCatalogThemes(theme);
            var shell = CatalogCases.CreateShell();
            var navigation = CatalogCases.Navigation(shell);
            navigation.PageTransition = null;
            show(new PageNavigationHost { Page = shell });
            await Task.Delay(100);
            // MainView applies the system variant on Loaded. Select the test variant afterwards.
            ApplyVariant(dark, compact);
            foreach (var item in CatalogCases.All(shell))
            {
                var visuals = 0;
                string? error = null;
                try
                {
                    var control = item.Create();
                    await navigation.ReplaceAsync(control as Page ?? new ContentPage { Content = control, Header = item.Name }, null);
                    // Settings initializes its selector to the system variant. Restore the
                    // requested matrix configuration after page initialization, before rendering.
                    await Task.Delay(25);
                    ApplyVariant(dark, compact);
                    // Allow the real platform's layout/render loop to process the newly selected page.
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        await Task.Delay(25);
                        if (control.Bounds.Width > 0 && control.Bounds.Height > 0 && control.GetVisualDescendants().Any())
                            break;
                    }
                    visuals = control.GetVisualDescendants().Count();
                    if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || visuals == 0)
                        throw new InvalidOperationException("The selected sample has no realized layout or visual content.");
                    VerifyConfiguration(shell, control, theme, dark, compact);
                    VerifyBindings(control);
                }
                catch (Exception exception) { error = exception.ToString(); }
                results.Add(new(theme.ToString(), dark ? "Dark" : "Light", compact ? "Compact" : "Normal",
                    item.Name, item.Kind, visuals, error));
                Console.WriteLine($"XAMLG CATALOG {(error is null ? "PASS" : "FAIL")} {theme} {dark} {compact} {item.Name}");
                if (error != null) Console.WriteLine(error);
            }
        }
        return results;
    }

    public static string Serialize(List<CatalogValidationResult> results) =>
        JsonSerializer.Serialize(results, CatalogValidationJsonContext.Default.ListCatalogValidationResult);

    public static void ApplyVariant(bool dark, bool compact)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ((FluentTheme)Application.Current.Resources["FluentTheme"]!).DensityStyle =
            compact ? DensityStyle.Compact : DensityStyle.Normal;
    }

    public static void VerifyConfiguration(MainView shell, Control page, CatalogTheme theme, bool dark, bool compact)
    {
        var app = Application.Current!;
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var density = compact ? DensityStyle.Compact : DensityStyle.Normal;
        if (App.CurrentTheme != theme || app.RequestedThemeVariant != variant ||
            shell.ActualThemeVariant != variant || page.ActualThemeVariant != variant ||
            ((FluentTheme)app.Resources["FluentTheme"]!).DensityStyle != density)
            throw new InvalidOperationException($"Expected {theme}/{variant}/{density}; actual theme {App.CurrentTheme}, " +
                $"application {app.RequestedThemeVariant}, shell {shell.ActualThemeVariant}, page {page.ActualThemeVariant}.");
    }

    public static void VerifyBindings(Control control)
    {
        if (control is CalendarDatePickerPage)
        {
            if (control.DataContext is not ViewModels.MainWindowViewModel model)
                throw new InvalidOperationException("CalendarDatePicker requires the shell view model, found " +
                    (control.DataContext?.GetType().FullName ?? "null") + ".");
            var picker = control.FindControl<CalendarDatePicker>("ValidationDatePicker") ??
                throw new InvalidOperationException("The validation date picker must be in the page's name scope.");
            var date = new DateTime(2030, 5, 4);
            model.ValidatedDateExample = date;
            if (picker.SelectedDate != date)
                throw new InvalidOperationException("The compiled date binding must update from the view model.");
            picker.SetCurrentValue(CalendarDatePicker.SelectedDateProperty, date.AddDays(1));
            if (model.ValidatedDateExample != date.AddDays(1))
                throw new InvalidOperationException("The compiled date binding must update the view model.");
            model.ValidatedDateExample = null;
        }
        if (control is HomePage && !control.GetVisualDescendants().OfType<ItemsControl>()
                .Any(items => items.ItemsSource is IReadOnlyList<HomeSection> { Count: 11 }))
            throw new InvalidOperationException("Home must resolve the full shell's view model and bind all eleven sections.");
        if (control is ComboBoxPage)
        {
            var editable = control.GetVisualDescendants().OfType<ComboBox>().Single(combo => combo.IsEditable);
            var binding = TextSearch.GetTextBinding(editable) ??
                throw new InvalidOperationException("TextSearch.TextBinding must retain its binding object.");
            var text = new TextBlock { DataContext = new ViewModels.IdAndName { SearchText = "search value" } };
            using var subscription = text.Bind(TextBlock.TextProperty, binding);
            if (text.Text != "search value")
                throw new InvalidOperationException("TextSearch must evaluate the binding against the item data context.");
        }
    }
}
