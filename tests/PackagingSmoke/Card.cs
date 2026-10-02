namespace PackagingSmoke;

public partial class Card : Panel
{
    public Card() => InitializeComponent();
    public int Clicks { get; private set; }
    public void ClickGeneratedButton() => action.Raise();
    private void OnClick(object? sender, EventArgs args) => Clicks++;
}
