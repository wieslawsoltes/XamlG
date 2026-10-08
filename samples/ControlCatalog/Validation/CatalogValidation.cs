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
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            ((FluentTheme)Application.Current.Resources["FluentTheme"]!).DensityStyle =
                compact ? DensityStyle.Compact : DensityStyle.Normal;
            foreach (var item in CatalogCases.All(shell))
            {
                var visuals = 0;
                string? error = null;
                try
                {
                    var control = item.Create();
                    await navigation.ReplaceAsync(control as Page ?? new ContentPage { Content = control, Header = item.Name }, null);
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
                    VerifyBindings(control);
                }
                catch (Exception exception) { error = exception.ToString(); }
                results.Add(new(theme.ToString(), dark ? "Dark" : "Light", compact ? "Compact" : "Normal",
                    item.Name, item.Kind, visuals, error));
                Console.WriteLine($"XAMLG CATALOG {(error is null ? "PASS" : "FAIL")} {theme} {dark} {compact} {item.Name}");
            }
        }
        return results;
    }

    public static string Serialize(List<CatalogValidationResult> results) =>
        JsonSerializer.Serialize(results, CatalogValidationJsonContext.Default.ListCatalogValidationResult);

    public static void VerifyBindings(Control control)
    {
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
