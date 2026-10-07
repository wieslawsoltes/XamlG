using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Metadata;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RemainingBindingTests
{
    private const string Ns = ResourceProjectFixture.Namespace + " xmlns:t='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";

    [AvaloniaTheory]
    [InlineData("Child?.Name")]
    [InlineData("Child?.Child?.Name")]
    public void NullConditionalPropertiesRetainNullResultsAndLiveUpdates(string path)
    {
        var xaml = "<TextBlock " + Ns + " x:DataType='t:BindingFixtureModel' Text='{CompiledBinding " + path + ", TargetNullValue=null-result, FallbackValue=failed}'/>";
        foreach (var root in Both<TextBlock>(xaml))
        {
            var model = new BindingFixtureModel(); root.DataContext = model;
            Assert.Equal("null-result", root.Text);
            model.Child = new BindingFixtureModel { Name = "value", Child = new BindingFixtureModel { Name = "value" } };
            Assert.Equal("value", root.Text);
            model.Child = null;
            Assert.Equal("null-result", root.Text);
        }
    }

    [AvaloniaTheory]
    [InlineData("Zero", "zero", false)]
    [InlineData("One", "string:value", true)]
    [InlineData("Overloaded", "object:value", true)]
    [InlineData("Preferred", "string:value", true)]
    [InlineData("ReturnValue", "return", false)]
    [InlineData("Inherited", "derived:value", true)]
    public void MethodValuesBindAsTypedDelegates(string path, string expected, bool parameter)
    {
        var xaml = "<ContentControl " + Ns + " x:DataType='t:MethodBindingModel' Content='{CompiledBinding " + path + "}'/>";
        foreach (var root in Both<ContentControl>(xaml))
        {
            var model = new MethodBindingModel(); root.DataContext = model;
            var method = Assert.IsAssignableFrom<Delegate>(root.Content);
            var result = method.DynamicInvoke(parameter ? new object[] { "value" } : Array.Empty<object>());
            Assert.Equal(expected, model.Last);
            if (path == "ReturnValue") Assert.Equal(42, result);
        }
    }

    [AvaloniaTheory]
    [InlineData("Zero", "zero")]
    [InlineData("One", "string:value")]
    [InlineData("Overloaded", "object:value")]
    [InlineData("Preferred", "string:value")]
    [InlineData("ReturnValue", "return")]
    [InlineData("Inherited", "derived:value")]
    public void CommandsUseTheUpstreamOverloadAndCanExecuteContracts(string path, string expected)
    {
        var xaml = "<Button " + Ns + " x:DataType='t:MethodBindingModel' Command='{CompiledBinding " + path + "}'/>";
        foreach (var root in Both<Button>(xaml))
        {
            var model = new MethodBindingModel(); root.DataContext = model;
            var command = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(root.Command);
            Assert.True(command.CanExecute("value"));
            command.Execute("value"); Assert.Equal(expected, model.Last);
            if (path is "Zero" or "One")
            {
                var changes = 0; command.CanExecuteChanged += (_, _) => changes++;
                model.Enabled = false;
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.False(command.CanExecute("value")); Assert.True(changes > 0);
            }
        }
    }

    [AvaloniaFact]
    public void NullableMethodPathsProduceDelegatesAfterTheSourceAppears()
    {
        var xaml = "<ContentControl " + Ns + " x:DataType='t:MethodBindingModel' Content='{CompiledBinding Child?.Zero}'/>";
        foreach (var root in Both<ContentControl>(xaml))
        {
            var model = new MethodBindingModel(); root.DataContext = model; Assert.Null(root.Content);
            model.Child = new MethodBindingModel();
            Assert.IsAssignableFrom<Delegate>(root.Content).DynamicInvoke();
            Assert.Equal("zero", model.Child.Last);
        }
    }

    [AvaloniaTheory]
    [InlineData("Ambiguous")]
    [InlineData("TooMany")]
    [InlineData("Child?.")]
    [InlineData("Child?Zero")]
    [InlineData("?.Zero")]
    public void InvalidMethodAndConditionalPathsRemainCompileErrors(string path)
    {
        var xaml = "<ContentControl " + Ns + " x:DataType='t:MethodBindingModel' Content='{CompiledBinding " + path + "}'/>";
        Assert.NotNull(AvaloniaUpstreamCompilation.Compile(xaml).Error);
        Assert.False(new ResourceProjectFixture(new[] { ("Binding.axaml", xaml) }).Result.Success);
    }

    [AvaloniaFact]
    public void ALeadingStreamOperatorUsesTheDataContextAsItsSource()
    {
        var xaml = "<TextBlock " + Ns + " x:DataType='t:StreamBindingModel' Text='{CompiledBinding ^}'/>";
        foreach (var root in Both<TextBlock>(xaml))
        { root.DataContext = new StreamBindingModel(); Assert.Equal("streamed", root.Text); }
    }

    private static IEnumerable<T> Both<T>(string xaml)
    {
        var baseline = AvaloniaUpstreamCompilation.Compile(xaml); Assert.Null(baseline.Error);
        yield return Assert.IsType<T>(baseline.Root);
        yield return Assert.IsType<T>(AvaloniaCompilation.Build(xaml));
    }
}

public class MethodBindingBase
{
    public string? Last { get; protected set; }
    public virtual void Inherited(string value) => Last = "base:" + value;
}

public sealed class MethodBindingModel : MethodBindingBase, INotifyPropertyChanged
{
    private bool _enabled = true;
    private MethodBindingModel? _child;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
    public MethodBindingModel? Child { get => _child; set { _child = value; PropertyChanged?.Invoke(this, new(nameof(Child))); } }
    public void Zero() => Last = "zero";
    public void One(string value) => Last = "string:" + value;
    public void Overloaded() => Last = "zero";
    public void Overloaded(string value) => Last = "string:" + value;
    public void Overloaded(object value) => Last = "object:" + value;
    public void Preferred() => Last = "zero";
    public void Preferred(string value) => Last = "string:" + value;
    public int ReturnValue() { Last = "return"; return 42; }
    public override void Inherited(string value) => Last = "derived:" + value;
    public void Ambiguous(string value) { }
    public void Ambiguous(int value) { }
    public void TooMany(string one, string two) { }
    [DependsOn(nameof(Enabled))] public bool CanZero(object? value) => Enabled;
    [DependsOn(nameof(Enabled))] public bool CanOne(object? value) => Enabled;
}

public sealed class StreamBindingModel : IObservable<string>
{
    public IDisposable Subscribe(IObserver<string> observer) { observer.OnNext("streamed"); return new Subscription(); }
    private sealed class Subscription : IDisposable { public void Dispose() { } }
}
