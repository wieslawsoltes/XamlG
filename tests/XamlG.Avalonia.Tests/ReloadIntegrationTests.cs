using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlG.AvaloniaRuntime;
using XamlG.Runtime;
using XamlG.Runtime.Reload;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class ReloadIntegrationTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    [AvaloniaFact]
    public void StructuralReloadPreservesNamedInputButNewSourceWins()
    {
        string View(string text, bool extra) => "<StackPanel " + Ns + ">" + (extra ? "<TextBlock Text='Added control'/>" : "") + "<TextBox x:Name='input' Text='" + text + "'/></StackPanel>";
        var old = (StackPanel)AvaloniaCompilation.Build(View("source", false));
        var input = (TextBox)old.Children[0]; input.Text = "user text";
        var host = new XamlReloadSession(old, new[] { new AvaloniaInteractiveStateAdapter() });
        var window = new Window { Content = old };
        try
        {
            window.Show(); window.UpdateLayout(); input.Focus(); input.CaretIndex = 4;
            var next = (StackPanel)AvaloniaCompilation.Build(View("source", true));
            Assert.True(host.Reload(0, () => next, value => window.Content = value).Applied);
            Assert.Equal("user text", ((TextBox)next.Children[1]).Text);
            Assert.Equal(4, ((TextBox)next.Children[1]).CaretIndex);
            var edited = (StackPanel)AvaloniaCompilation.Build(View("new declaration", true));
            Assert.True(host.Reload(1, () => edited, value => window.Content = value).Applied);
            Assert.Equal("new declaration", ((TextBox)edited.Children[1]).Text);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void RealizedVisualsCarryTheirNearestSourceMapping()
    {
        var root = (Button)AvaloniaCompilation.Build("<Button " + Ns + " x:Name='button' Content='Mapped'/>");
        var window = new Window { Content = root };
        try
        {
            window.Show(); window.UpdateLayout();
            var tree = AvaloniaVisualInspector.Inspect(root);
            Assert.NotNull(tree.Source); Assert.True(tree.IsSourceOwned); Assert.NotEmpty(tree.Children);
            Assert.Equal(tree.Source, tree.Children[0].Source);
            Assert.True(XamlRuntimeSession.TryGet(root, out var session));
            Assert.Same(root, session!.FindNode(root)!.Instance);
        }
        finally { window.Close(); }
    }
}
