using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace XamlG.AvaloniaRuntime;

/// <summary>Loads resources already compiled into referenced Avalonia libraries.</summary>
public static class AvaloniaReferencedResource
{
    [RequiresUnreferencedCode("Referenced Avalonia libraries must preserve their precompiled resource loaders.")]
    public static IStyle LoadStyle(IServiceProvider services, string uri) =>
        (IStyle)AvaloniaXamlLoader.Load(services, new Uri(uri, UriKind.Absolute));

    [RequiresUnreferencedCode("Referenced Avalonia libraries must preserve their precompiled resource loaders.")]
    public static ResourceDictionary LoadDictionary(IServiceProvider services, string uri) =>
        (ResourceDictionary)AvaloniaXamlLoader.Load(services, new Uri(uri, UriKind.Absolute));
}
