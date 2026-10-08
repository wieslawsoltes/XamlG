using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ControlCatalog.Validation;

namespace ControlCatalog.Desktop;

internal static class CatalogValidationHost
{
    public static void Start(string output)
    {
        DispatcherTimer.RunOnce(async () =>
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            try
            {
                var results = await CatalogValidation.RunAsync(content => lifetime.MainWindow!.Content = content);
                File.WriteAllText(output, CatalogValidation.Serialize(results));
                lifetime.Shutdown(results.Any(result => result.Error != null) ? 1 : 0);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                lifetime.Shutdown(1);
            }
        }, TimeSpan.FromSeconds(1));
    }
}
