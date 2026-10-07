using Avalonia.Markup.Xaml;

namespace XamlG.Avalonia.Tests;

internal sealed record AvaloniaUpstreamCompilation(object? Root, IReadOnlyList<RuntimeXamlDiagnostic> Diagnostics, Exception? Error)
{
    public static AvaloniaUpstreamCompilation Compile(string xaml)
    {
        var document = new RuntimeXamlLoaderDocument(xaml);
        var diagnostics = new List<RuntimeXamlDiagnostic>();
        try
        {
            var root = AvaloniaRuntimeXamlLoader.Load(document, new RuntimeXamlLoaderConfiguration
            {
                LocalAssembly = typeof(AvaloniaUpstreamCompilation).Assembly,
                UseCompiledBindingsByDefault = true,
                DiagnosticHandler = diagnostic => { diagnostics.Add(diagnostic); return diagnostic.Severity; }
            });
            return new(root, diagnostics, null);
        }
        catch (Exception error) { return new(null, diagnostics, error); }
        finally { document.XamlStream.Dispose(); }
    }
}
