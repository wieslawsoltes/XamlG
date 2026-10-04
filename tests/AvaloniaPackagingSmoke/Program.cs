using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
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
    Require(view.Action.Width == 144, "The compiled StyleInclude/selector/typed setter did not apply.");
    Require(view.Action.Background is ISolidColorBrush brush && brush.Color == Color.Parse("#336699"),
        "The compiled MergeResourceInclude did not supply the static resource.");
    var exports = typeof(SmokeView).Assembly.GetCustomAttributes(typeof(XamlCompiledResourceAttribute), false);
    Require(exports.Length == 2, "The library resource/style factories were not exported in assembly metadata.");
    Require(XamlRuntimeSession.TryGet(view, out var session), "The generated root has no runtime session.");
    session!.Dispose();
    var retained = view.Output.Text;
    model.Name = "after disposal";
    Require(view.Output.Text == retained, "The retired graph retained a live binding subscription.");
    Console.WriteLine("PASS: installed Avalonia generator, names, compiled/two-way binding, project includes, resource exports, typed styles and subscription retirement.");
}
finally { window.Close(); }

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
