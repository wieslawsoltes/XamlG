using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XamlG.ProjectSystem;

public sealed record WorkspaceTemplate(string Id, string Name, string Description, string Category);
public sealed record ProjectTemplateRequest(string Template, string Name, string? Directory = null, string? Namespace = null,
    string Framework = "net10.0", string AvaloniaVersion = "12.1.3", string? XamlGVersion = null);
public sealed record WorkspaceTemplatePlan(string EntryPath, ImmutableDictionary<string, WorkspaceFile> Files)
{
    public IEnumerable<WorkspaceChange> Changes => Files.Select(pair => new WorkspaceChange(pair.Key, null, pair.Value));
    public VirtualWorkspace CreateWorkspace() => new(Files, EntryPath);
}

/// <summary>Offline starters, not a substitute for the installed .NET template engine.</summary>
public static class WorkspaceTemplates
{
    public static ImmutableArray<WorkspaceTemplate> All { get; } =
    [
        new("console", "Console App", "C# application with an SDK project and top-level entry point.", ".NET"),
        new("classlib", "Class Library", "Reusable C# library with nullable reference types.", ".NET"),
        new("avalonia.app", "Avalonia App", "Desktop application with App and MainWindow XAML/code-behind.", "Avalonia"),
        new("avalonia.mvvm", "Avalonia MVVM App", "Desktop application with a typed view model and compiled bindings.", "Avalonia")
    ];
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while").Split(' '), StringComparer.Ordinal);

    public static WorkspaceTemplatePlan CreateSolution(string name, ProjectTemplateRequest? initialProject = null)
    {
        WorkspacePath.ValidateName(name);
        var files = ImmutableDictionary.CreateBuilder<string, WorkspaceFile>(StringComparer.Ordinal);
        var solution = new XElement("Solution");
        if (initialProject != null)
        {
            var project = CreateProject(initialProject);
            foreach (var pair in project.Files) files.Add(pair.Key, pair.Value);
            solution.Add(new XElement("Project", new XAttribute("Path", project.EntryPath)));
        }
        var path = name + ".slnx";
        files.Add(path, new(new XDocument(solution).ToString() + "\n"));
        var plan = new WorkspaceTemplatePlan(path, files.ToImmutable());
        _ = plan.CreateWorkspace(); // Reject cross-platform collisions before publishing the plan.
        return plan;
    }

    public static WorkspaceTemplatePlan CreateProject(ProjectTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkspacePath.ValidateName(request.Name);
        if (!All.Any(template => template.Id == request.Template)) throw new ArgumentException("Unknown offline project template.");
        var framework = request.Framework;
        if (framework is not ("net8.0" or "net9.0" or "net10.0") && !(request.Template == "classlib" && framework is "netstandard2.0" or "netstandard2.1"))
            throw new ArgumentException("Choose a supported offline target framework, or use the native SDK template engine.");
        var directory = request.Directory ?? request.Name;
        if (directory.Length != 0) directory = WorkspacePath.Normalize(directory);
        var ns = request.Namespace ?? NamespaceFor(request.Name);
        ValidateNamespace(ns);
        var files = ImmutableDictionary.CreateBuilder<string, WorkspaceFile>(StringComparer.Ordinal);
        string FilePath(string name) => WorkspacePath.Normalize(directory.Length == 0 ? name : directory + "/" + name);
        void Add(string name, string text) => files.Add(FilePath(name), new(text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n"));
        var properties = new XElement("PropertyGroup", new XElement("TargetFramework", framework), new XElement("RootNamespace", ns),
            new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"), new XElement("LangVersion", "latest"));
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties);
        if (request.Template == "console")
        {
            properties.Add(new XElement("OutputType", "Exe"));
            Add("Program.cs", "Console.WriteLine(\"Hello from " + request.Name + "!\");");
        }
        else if (request.Template == "classlib")
            Add("Class1.cs", $$"""
                namespace {{ns}};

                public class Class1
                {
                }
                """);
        else
        {
            ValidateVersion(request.AvaloniaVersion);
            var mvvm = request.Template == "avalonia.mvvm";
            properties.Add(new XElement("OutputType", "WinExe"), new XElement("AvaloniaUseCompiledBindingsByDefault", "true"));
            var packages = new XElement("ItemGroup");
            foreach (var id in new[] { "Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent" })
                packages.Add(new XElement("PackageReference", new XAttribute("Include", id), new XAttribute("Version", request.AvaloniaVersion)));
            if (!string.IsNullOrWhiteSpace(request.XamlGVersion))
            {
                ValidateVersion(request.XamlGVersion);
                packages.Add(new XElement("PackageReference", new XAttribute("Include", "XamlG.Avalonia"), new XAttribute("Version", request.XamlGVersion)));
            }
            project.Add(packages);
            Add("Program.cs", $$"""
                using Avalonia;

                namespace {{ns}};

                internal static class Program
                {
                    [STAThread]
                    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

                    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
                }
                """);
            Add("App.axaml", $$"""
                <Application xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             x:Class="{{ns}}.App" RequestedThemeVariant="Default">
                    <Application.Styles>
                        <FluentTheme />
                    </Application.Styles>
                </Application>
                """);
            Add("App.axaml.cs", $$"""
                using Avalonia;
                using Avalonia.Controls.ApplicationLifetimes;
                using Avalonia.Markup.Xaml;

                namespace {{ns}};

                public partial class App : Application
                {
                    public override void Initialize() => AvaloniaXamlLoader.Load(this);

                    public override void OnFrameworkInitializationCompleted()
                    {
                        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                            desktop.MainWindow = new MainWindow();
                        base.OnFrameworkInitializationCompleted();
                    }
                }
                """);
            var dataType = mvvm ? $"xmlns:vm=\"using:{ns}.ViewModels\" x:DataType=\"vm:MainWindowViewModel\"" : "";
            var text = mvvm ? "{Binding Greeting}" : "Welcome to Avalonia";
            // Names are XML attribute data, not XAML syntax. Escape both XML
            // metacharacters and leading markup-extension braces as literal text.
            var title = new XAttribute("Title", "{}" + request.Name);
            Add("MainWindow.axaml", $$"""
                <Window xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        x:Class="{{ns}}.MainWindow" {{dataType}}
                        Width="900" Height="600" {{title}}>
                    <Grid>
                        <TextBlock Text="{{text}}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="24" />
                    </Grid>
                </Window>
                """);
            var dataContext = mvvm ? "\n        DataContext = new ViewModels.MainWindowViewModel();" : "";
            Add("MainWindow.axaml.cs", $$"""
                using Avalonia.Controls;
                using Avalonia.Markup.Xaml;

                namespace {{ns}};

                public partial class MainWindow : Window
                {
                    public MainWindow()
                    {
                        AvaloniaXamlLoader.Load(this);{{dataContext}}
                    }
                }
                """);
            if (mvvm) Add("ViewModels/MainWindowViewModel.cs", $$"""
                using System.ComponentModel;
                using System.Runtime.CompilerServices;

                namespace {{ns}}.ViewModels;

                public class MainWindowViewModel : INotifyPropertyChanged
                {
                    private string _greeting = "Welcome to Avalonia";
                    public string Greeting
                    {
                        get => _greeting;
                        set
                        {
                            if (_greeting == value) return;
                            _greeting = value;
                            OnPropertyChanged();
                        }
                    }
                    public event PropertyChangedEventHandler? PropertyChanged;
                    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null) =>
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
                }
                """);
        }
        var projectPath = request.Name + ".csproj";
        Add(projectPath, new XDocument(project).ToString());
        var plan = new WorkspaceTemplatePlan(FilePath(projectPath), files.ToImmutable());
        _ = plan.CreateWorkspace();
        return plan;
    }

    public static string NamespaceFor(string name)
    {
        var parts = name.Split('.').Select(part =>
        {
            var result = new string(part.Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());
            if (result.Length == 0 || char.IsDigit(result[0]) || Keywords.Contains(result)) result = "_" + result;
            return result;
        });
        return string.Join('.', parts);
    }

    private static void ValidateNamespace(string value)
    {
        if (value.Length > 256 || value.Split('.').Any(part => part.Length == 0 || !(char.IsAsciiLetter(part[0]) || part[0] == '_') ||
            part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') || Keywords.Contains(part)))
            throw new ArgumentException("Enter a C# namespace using dot-separated, non-keyword identifiers.");
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length > 100 || !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("Use an explicit semantic package version.");
    }
}
