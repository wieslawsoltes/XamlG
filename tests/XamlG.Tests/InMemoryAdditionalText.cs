using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace XamlG.Tests;

internal sealed class InMemoryAdditionalText(string path, string text) : AdditionalText
{
    private readonly SourceText _text = SourceText.From(text, Encoding.UTF8);
    public override string Path { get; } = path;
    public override SourceText GetText(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _text;
    }
}
