using System.Reflection;
using XamlG.Frameworks;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SpanEnumParserTests
{
    private delegate bool TryEnum<T>(ReadOnlySpan<char> text, bool ignoreCase, out T value);

    [Theory]
    [InlineData("Key")]
    [InlineData("KeyModifiers")]
    [InlineData("TextDecorationLocation")]
    public void NamedSpanEnumsRetainBclRulesWithoutAllocating(string name)
    {
        var type = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Frameworks.Avalonia.Parsing." + name, true)!;
        typeof(SpanEnumParserTests).GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, null);
    }

    private static void Check<T>() where T : struct
    {
        var method = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Frameworks.Avalonia.Parsing.SpanEnumParser", true)!
            .GetMethod("TryParse")!.MakeGenericMethod(typeof(T));
        var parse = (TryEnum<T>)method.CreateDelegate(typeof(TryEnum<T>));
        var names = Enum.GetNames(typeof(T));
        var inputs = names.SelectMany(name => new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant(), " " + name + "\t" })
            .Concat(new[] { "", " ", "0", "-1", "+1", "2147483647", "2147483648", "-2147483649", "4294967295", "1\0",
                names[0] + "," + names[^1], names[0] + ",", "not-an-enum", "," });
        foreach (var text in inputs)
        foreach (var ignoreCase in new[] { false, true })
        {
            var expected = Enum.TryParse<T>(text, ignoreCase, out var expectedValue);
            Assert.Equal(expected, parse(("prefix" + text + "suffix").AsSpan(6, text.Length), ignoreCase, out var actualValue));
            Assert.Equal(expectedValue, actualValue);
        }
        var token = names[^1].ToLowerInvariant();
        for (var i = 0; i < 1000; i++) parse(token.AsSpan(), true, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 4096; i++) if (parse(token.AsSpan(), true, out _)) count++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4096, count);
        Assert.Equal(0, allocated);
    }
}
