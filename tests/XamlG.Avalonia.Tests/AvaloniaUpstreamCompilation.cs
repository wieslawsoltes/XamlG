using Avalonia.Markup.Xaml;

namespace XamlG.Avalonia.Tests;

internal sealed record AvaloniaUpstreamCompilation(object? Root, IReadOnlyList<RuntimeXamlDiagnostic> Diagnostics, Exception? Error)
{
    public static AvaloniaUpstreamCompilation Compile(string xaml, bool createSourceInfo = false, string? documentPath = null, object? rootInstance = null, string? baseUri = null, IServiceProvider? services = null)
    {
        var document = new RuntimeXamlLoaderDocument(rootInstance, xaml) { Document = documentPath, BaseUri = baseUri == null ? null : new Uri(baseUri), ServiceProvider = services };
        var diagnostics = new List<RuntimeXamlDiagnostic>();
        try
        {
            var root = AvaloniaRuntimeXamlLoader.Load(document, new RuntimeXamlLoaderConfiguration
            {
                LocalAssembly = typeof(AvaloniaUpstreamCompilation).Assembly,
                UseCompiledBindingsByDefault = true,
                CreateSourceInfo = createSourceInfo,
                DiagnosticHandler = diagnostic => { diagnostics.Add(diagnostic); return diagnostic.Severity; }
            });
            return new(root, diagnostics, null);
        }
        catch (Exception error) { return new(null, diagnostics, error); }
        finally { document.XamlStream.Dispose(); }
    }
}
