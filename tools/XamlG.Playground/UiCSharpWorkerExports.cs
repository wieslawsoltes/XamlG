using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using XamlG.IntelligentUI;

namespace XamlG.Playground;

/// <summary>Dedicated .NET worker entry point. It never runs Program.Main, Blazor, Avalonia,
/// the coding agent or application services. Only the opaque supervisor initializes it.</summary>
[SupportedOSPlatform("browser")]
public static partial class UiCSharpWorkerExports
{
    private static UiCSharpWorkerSession? _session;
    private static bool _initializing;

    [JSExport]
    public static async Task Initialize(string assetBase)
    {
        if (_session != null || _initializing) throw new InvalidOperationException("The worker was already initialized.");
        if (!Uri.TryCreate(assetBase, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Invalid execution asset base.", nameof(assetBase));
        _initializing = true;
        using var http = new HttpClient { BaseAddress = uri };
        var compiler = new BrowserCompilerService(http);
        await compiler.InitializeAsync();
        var references = compiler.Analyze("<StackPanel xmlns=\"https://github.com/avaloniaui\"/>", "").Compilation.References;
        // The exact declaration has already been reviewed by the owner. These references and
        // every executable byte are confined to this disposable worker, not the parent editor.
        _session = new(references, _ => true);
    }

    [JSExport]
    public static string Dispatch(string method, string json)
        => (_session ?? throw new InvalidOperationException("The worker is not initialized.")).Dispatch(method, json);
}
