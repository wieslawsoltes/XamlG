using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XamlG.Avalonia.Tests;

public sealed class BindingFixtureModel : INotifyPropertyChanged
{
    private string _name = "initial";
    private BindingFixtureModel? _child;
    private bool _enabled = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get => _name; set { _name = value; Changed(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
    public BindingFixtureModel? Child { get => _child; set { _child = value; Changed(); } }
    public ObservableCollection<string> Items { get; } = new() { "first", "second" };
    public Dictionary<string, int> Lookup { get; } = new() { ["answer"] = 42 };
    public int[] Numbers { get; } = new[] { 7, 11 };
    public Task<string> Message { get; set; } = Task.FromResult("task result");
    public string ReadOnly => "read only";
    public int InvocationCount { get; private set; }
    public void Execute() => InvocationCount++;
    public bool CanExecute => Enabled;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
