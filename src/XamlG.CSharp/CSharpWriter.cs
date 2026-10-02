using System.Text;
namespace XamlG.CSharp;
internal sealed class CSharpWriter
{
    private readonly StringBuilder _text = new();
    public int Indent { get; set; }
    public int Position => _text.Length;
    public void Line(string text = "")
    {
        if (text.Length != 0 && !text.StartsWith("#", StringComparison.Ordinal)) _text.Append(' ', Indent * 4);
        _text.Append(text).Append('\n');
    }
    public void Open(string header) { Line(header); Line("{"); Indent++; }
    public void Close(string suffix = "") { Indent--; Line("}" + suffix); }
    public override string ToString() => _text.ToString();
}
