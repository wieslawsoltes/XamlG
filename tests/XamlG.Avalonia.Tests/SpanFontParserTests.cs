using System.Reflection;
using XamlG.Frameworks;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SpanFontParserTests
{
    [Fact]
    public void FontFeaturesRetainRegexGrammarUnicodeAndOverflowDefaults()
    {
        var publicType = typeof(global::Avalonia.Media.FontFeature);
        var privateType = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Frameworks.Avalonia.Parsing.FontFeature", true)!;
        var samples = new List<string?> { null, "", "kern", "KERN", "+ kern", "-kern=1", "kern=ON", "kern=off", "kern[1 2]",
            "kern[2147483647]", "kern[2147483648]", "kern[1:2147483648]", "kern[١:٢]=٣", "kern[1\n:2]", "kern=off\n" };
        foreach (var tag in new[] { "kern", "aalt", "abc", "abcde", "éλ_a", "a\u0301bc", "Ⅷabc", "١abc", "a\u200cbc" })
        foreach (var sign in new[] { "", "+", "-", " + ", " - " })
        foreach (var range in new[] { "", "[]", "[:]", "[1]", "[:5]", "[3:]", "[1:2]", "[1 2]", "[ 1 : 2 ]", "[1::2]" })
        foreach (var value in new[] { "", "=0", "=1", "=on", "=off", "=ON", "=2147483648", "=١" })
            samples.Add(sign + tag + range + value);
        var random = new Random(1773);
        const string alphabet = "kernab_0123+-=onf[: ]\t\n\r\u0301\u00a0";
        for (var i = 0; i < 4000; i++)
            samples.Add(new string(Enumerable.Range(0, random.Next(0, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        Compare(publicType, privateType, samples, ["Tag", "Value", "Start", "End"]);
    }

    [Fact]
    public void UnicodeSegmentsRetainWildcardsFinalNewlinesAndFailureMessages()
    {
        var publicType = typeof(global::Avalonia.Media.FontFeature).Assembly.GetTypes().Single(type => type.Name == "UnicodeRangeSegment");
        var privateType = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Frameworks.Avalonia.Parsing.UnicodeRangeSegment", true)!;
        var samples = new List<string?> { null, "", "20", "U+20", "u+3F", "U+30??", "1?2?", "0-10FFFD", "20-10", "FFFFFF",
            "U+20\n", "U+20\n\n", "U+20\r\n", "20\n-3F\n", "U+?", "?0", "0?", "3?-4F", "3?-G0", "20-3?",
            " U+20 ", "0000000", "20-3F-FF", "#20", "U+20\0", "0?\n" };
        var random = new Random(9827);
        const string alphabet = "0123456789abcdefABCDEFuU+?- \t\n\r";
        for (var i = 0; i < 8000; i++)
            samples.Add(new string(Enumerable.Range(0, random.Next(0, 16)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        Compare(publicType, privateType, samples, ["Start", "End"]);
    }

    private static void Compare(Type publicType, Type privateType, IEnumerable<string?> inputs, string[] properties)
    {
        var reference = publicType.GetMethod("Parse", [typeof(string)])!;
        var parser = privateType.GetMethod("Parse", [typeof(string)])!;
        foreach (var input in inputs)
        {
            var expected = Invoke(reference, input);
            var actual = Invoke(parser, input);
            Assert.True(expected.Error?.GetType() == actual.Error?.GetType(), $"Exception mismatch for '{input}': {expected.Error} / {actual.Error}");
            if (expected.Error != null)
            {
                Assert.Equal(expected.Error.Message, actual.Error!.Message);
                continue;
            }
            foreach (var property in properties)
                Assert.True(Equals(publicType.GetProperty(property)!.GetValue(expected.Value), privateType.GetProperty(property)!.GetValue(actual.Value)),
                    $"{publicType.Name}.{property} differs for '{input}'");
        }
    }

    private static (object? Value, Exception? Error) Invoke(MethodInfo method, string? input)
    {
        try { return (method.Invoke(null, [input]), null); }
        catch (TargetInvocationException error) { return (null, error.InnerException!); }
    }
}
