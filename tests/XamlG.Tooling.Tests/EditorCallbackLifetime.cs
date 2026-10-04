namespace XamlG.Tooling.Tests;

internal sealed class EditorCallbackLifetime : IDisposable
{
    public int Disposals { get; private set; }
    public void Dispose() => Disposals++;
}
