using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ControlCatalog.Models;
using ControlCatalog.Pages;
using ControlCatalog.ViewModels;

namespace ControlCatalog.Validation;

/// <summary>Validation uses the same page and gallery factories as the catalog drawer.</summary>
public sealed record CatalogCase(string Name, string Kind, Func<Control> Create);

public static class CatalogCases
{
    public static IReadOnlyList<CatalogCase> All(MainView? shell = null)
    {
        var model = shell?.ViewModel ?? new MainWindowViewModel();
        var result = new List<CatalogCase>();
        Add(model.HomeItem);
        Add(model.SettingsItem);
        foreach (var section in model.HomeSections)
        {
            result.Add(new("Sections/" + section.Title, "section", () => section.PageItem.CreatePage()));
            foreach (var item in section.Items ?? Array.Empty<PageItem>())
                Add(item);
        }
        AddGallery("CarouselPage", CarouselDemoPage.ValidationDemos);
        AddGallery("CommandBar", CommandBarPage.ValidationDemos);
        AddGallery("ContentPage", ContentDemoPage.ValidationDemos);
        AddGallery("NavigationPage", NavigationDemoPage.ValidationDemos);
        AddGallery("DrawerPage", DrawerDemoPage.ValidationDemos);
        AddGallery("ConnectedAnimation", ConnectedAnimationDemoPage.ValidationDemos);
        AddGallery("TabbedPage", TabbedDemoPage.ValidationDemos);
        AddGallery("Gestures", GesturePage.ValidationDemos);
        AddGallery("PipsPager", PipsPagerPage.ValidationDemos);
        if (result.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("Catalog validation names must be unique.");
        return result;

        void Add(PageItem item)
        {
            result.Add(new(item.Header, "page", () => item.CreatePage()));
            foreach (var sample in item.Samples ?? [])
                result.Add(new(item.Header + "/" + sample.Title, "sample", sample.Factory));
        }

        void AddGallery(string name, IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> demos)
        {
            foreach (var demo in demos)
                result.Add(new(name + "/" + demo.Group + "/" + demo.Title, "gallery", demo.Factory));
        }
    }

    public static MainView CreateShell() => new() { DataContext = new MainWindowViewModel() };

    public static NavigationPage Navigation(MainView shell) => (NavigationPage)shell.ViewModel.Navigator!;
}
