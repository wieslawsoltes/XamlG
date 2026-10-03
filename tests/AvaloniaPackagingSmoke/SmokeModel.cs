using System.ComponentModel;

namespace AvaloniaPackagingSmoke;

public sealed class SmokeModel : INotifyPropertyChanged
{
    private string _name = "initial";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
}
