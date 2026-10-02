using XamlG.Runtime;

namespace PackagingSmoke;

public class Panel
{
    public string Title { get; set; } = string.Empty;
    [Content]
    public List<Button> Children { get; } = new();
}
