using System;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ControlCatalog.Validation;

internal partial class Program
{
    [JSExport]
    public static async Task<string> ValidateCatalog()
    {
        var lifetime = (ISingleViewApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var results = await CatalogValidation.RunAsync(content => lifetime.MainView = content);
        return CatalogValidation.Serialize(results);
    }
}
