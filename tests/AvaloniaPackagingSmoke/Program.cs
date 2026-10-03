using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaPackagingSmoke;
using XamlG.Runtime;

AppBuilder.Configure<SmokeApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var model = new SmokeModel();
var view = new SmokeView { DataContext = model };
var window = new Window { Content = view, Width = 400, Height = 240 };
try
{
    window.Show(); window.UpdateLayout();
    Require(view.Output.Text == "initial", "The generated compiled binding did not read the view model.");
    model.Name = "updated";
    Require(view.Output.Text == "updated", "The generated accessor did not observe property changes.");
    view.Input.Text = "user edit";
    Require(model.Name == "user edit", "The generated two-way setter did not update the view model.");
    Require(view.Action.Width == 144, "The compiled selector/typed setter did not apply.");
    Require(XamlRuntimeSession.TryGet(view, out var session), "The generated root has no runtime session.");
    session!.Dispose();
    var retained = view.Output.Text;
    model.Name = "after disposal";
    Require(view.Output.Text == retained, "The retired graph retained a live binding subscription.");
    Console.WriteLine("PASS: installed Avalonia generator, named fields, compiled/two-way binding, typed style setter, and subscription retirement.");
}
finally { window.Close(); }

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
