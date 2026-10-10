using Microsoft.CodeAnalysis.CSharp;
using XamlG.CSharp;
using Xunit;

namespace XamlG.Tests;

public sealed class EmissionLiteralFastPathTests
{
    [Fact]
    public void Every_utf16_code_unit_retains_Roslyns_exact_spelling()
    {
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var text = "prefix" + (char)code + "suffix";
            Assert.Equal(SymbolDisplay.FormatLiteral(text, true), CSharpNames.Literal(text));
        }
    }

    [Fact]
    public void Mixed_escapes_surrogates_and_long_literals_match_Roslyn()
    {
        var random = new Random(78413);
        var samples = new List<string>
        {
            "", "a'b", "\\\"\\n", "\r\n\t\0", "\ud800\udfff", "\udfff\ud800", "\u2028\u2029\u0085",
            new string('a', 65536), new string('a', 65536) + "\n", "\n" + new string('a', 65536)
        };
        for (var i = 0; i < 512; i++)
            samples.Add(new string(Enumerable.Range(0, random.Next(128)).Select(_ => (char)random.Next(65536)).ToArray()));
        foreach (var text in samples)
            Assert.Equal(SymbolDisplay.FormatLiteral(text, true), CSharpNames.Literal(text));
        Assert.Throws<ArgumentNullException>(() => CSharpNames.Literal(null!));
    }
}
