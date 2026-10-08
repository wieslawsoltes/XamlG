using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace ControlCatalog.Pages;

// Access the original registries without copying or changing their factories.
public partial class CarouselDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class CommandBarPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class ContentDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class NavigationDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class DrawerDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class ConnectedAnimationDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class TabbedDemoPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class GesturePage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}

public partial class PipsPagerPage
{
    internal static IReadOnlyList<(string Group, string Title, string Description, Func<UserControl> Factory)> ValidationDemos => Demos;
}
