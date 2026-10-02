namespace PackagingSmoke;

public sealed class Button
{
    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public event EventHandler? Click;
    public void Raise() => Click?.Invoke(this, EventArgs.Empty);
}
