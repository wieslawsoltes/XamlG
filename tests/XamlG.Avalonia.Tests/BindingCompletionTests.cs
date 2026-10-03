using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class BindingCompletionTests
{
    private const string Ns = "xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:vm='clr-namespace:XamlG.Avalonia.Tests;assembly=XamlG.Avalonia.Tests'";
    private const string Typed = " x:DataType='vm:BindingFixtureModel'";

    [AvaloniaFact]
    public void ObjectElementBindingsUseTheSameCheckedPathAndSetter()
    {
        var view = (TextBox)AvaloniaCompilation.Build("<TextBox " + Ns + Typed + "><TextBox.Text><CompiledBinding Path='Name' Mode='TwoWay' UpdateSourceTrigger='PropertyChanged'/></TextBox.Text></TextBox>");
        var model = new BindingFixtureModel { Name = "from object element" };
        view.DataContext = model;
        Assert.Equal(model.Name, view.Text);
        view.Text = "from editor";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("from editor", model.Name);
    }

    [AvaloniaFact]
    public void BindingPropertyElementsSurviveTheirCompiledOwnerTypeChange()
    {
        var view = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + "><TextBlock.Text><Binding><Binding.Path>Name</Binding.Path><Binding.Mode>OneWay</Binding.Mode></Binding></TextBlock.Text></TextBlock>");
        view.DataContext = new BindingFixtureModel { Name = "normalized owner" };
        Assert.Equal("normalized owner", view.Text);
    }

    [AvaloniaTheory]
    [InlineData("Visual")]
    [InlineData("Logical")]
    public void RelativeAncestorUsesTheRequestedTree(string tree)
    {
        var view = (Border)AvaloniaCompilation.Build("<Border " + Ns + " Tag='ancestor'><TextBlock Text='{CompiledBinding Tag, RelativeSource={RelativeSource FindAncestor, AncestorType=Border, AncestorLevel=1, Tree=" + tree + "}}'/></Border>");
        var window = new Window { Content = view };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("ancestor", Assert.IsType<TextBlock>(view.Child).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RelativeSelfWorksAsAnObjectElement()
    {
        var view = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + " Tag='object source'><TextBlock.Text><CompiledBinding Path='Tag'><CompiledBinding.RelativeSource><RelativeSource Mode='Self'/></CompiledBinding.RelativeSource></CompiledBinding></TextBlock.Text></TextBlock>");
        Assert.Equal("object source", view.Text);
    }

    [AvaloniaFact]
    public void ElementNameArgumentHasAStaticallyTypedForwardSource()
    {
        var view = (StackPanel)AvaloniaCompilation.Build("<StackPanel " + Ns + "><TextBlock Text='{CompiledBinding Text, ElementName=input}'/><TextBox x:Name='input' Text='forward source'/></StackPanel>");
        var window = new Window { Content = view };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("forward source", ((TextBlock)view.Children[0]).Text);
            ((TextBox)view.Children[1]).Text = "changed source";
            Assert.Equal("changed source", ((TextBlock)view.Children[0]).Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CommandBindingRetainsItsFinalTargetTypeWhileBindingAsObject()
    {
        var view = (Button)AvaloniaCompilation.Build("<Button " + Ns + Typed + " Command='{CompiledBinding Execute}'/>");
        var model = new BindingFixtureModel(); view.DataContext = model;
        Assert.NotNull(view.Command);
        Assert.True(view.Command.CanExecute(null));
        view.Command.Execute(null);
        Assert.Equal(1, model.InvocationCount);
        model.Enabled = false;
        Assert.False(view.Command.CanExecute(null));
    }

    [AvaloniaFact]
    public void DisposingTheGeneratedSessionUnsubscribesBindings()
    {
        var view = (TextBlock)AvaloniaCompilation.Build("<TextBlock " + Ns + Typed + " Text='{CompiledBinding Name}'/>");
        var model = new BindingFixtureModel { Name = "live" }; view.DataContext = model;
        Assert.Equal("live", view.Text);
        Assert.True(XamlRuntimeSession.TryGet(view, out var session));
        session!.Dispose();
        var retained = view.Text;
        model.Name = "should not reach the retired graph";
        Assert.Equal(retained, view.Text);
    }
}
