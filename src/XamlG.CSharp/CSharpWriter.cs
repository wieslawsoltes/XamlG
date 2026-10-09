using System.Text;
namespace XamlG.CSharp;
internal sealed class CSharpWriter
{
    private readonly StringBuilder _text = new();
    private readonly Stack<int> _scopes = new();
    private int _nextScope;
    public int ScopeId => _scopes.Count == 0 ? 0 : _scopes.Peek();
    public int Indent { get; set; }
    public int Position => _text.Length;
    public void Line(string text = "")
    {
        if (text.Length != 0 && !text.StartsWith("#", StringComparison.Ordinal)) _text.Append(' ', Indent * 4);
        _text.Append(text).Append('\n');
    }
    public void Open(string header) { Line(header); Line("{"); Indent++; _scopes.Push(++_nextScope); }
    public void Close(string suffix = "") { _scopes.Pop(); Indent--; Line("}" + suffix); }
    public void Prepend(string text) => _text.Insert(0, text);
    public void Append(string text) => _text.Append(text);
    public override string ToString() => _text.ToString();
}
