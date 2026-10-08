using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using ControlCatalog.Models;

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
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            ((FluentTheme)Application.Current.Resources["FluentTheme"]!).DensityStyle =
                compact ? DensityStyle.Compact : DensityStyle.Normal;
            var navigation = new NavigationPage { PageTransition = null };
            show(new PageNavigationHost { Page = navigation });
            foreach (var item in CatalogCases.All())
            {
                var visuals = 0;
                string? error = null;
                try
                {
                    var control = item.Create();
                    await navigation.ReplaceAsync(control as Page ?? new ContentPage { Content = control, Header = item.Name }, null);
                    // Allow the real platform's layout/render loop to process the newly selected page.
                    await Task.Delay(60);
                    visuals = control.GetVisualDescendants().Count();
                    if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || visuals == 0)
                        throw new InvalidOperationException("The selected sample has no realized layout or visual content.");
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
}
