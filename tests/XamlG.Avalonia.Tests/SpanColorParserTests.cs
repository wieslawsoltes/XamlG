using System.Reflection;
using XamlG.Frameworks;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class SpanColorParserTests
{
    private delegate bool TryColor<T>(ReadOnlySpan<char> text, out T value);

    [Theory]
    [InlineData("Color", "rgba(10%,20%,30%,40%)", "ARGB")]
    [InlineData("Color", "CornflowerBlue", "ARGB")]
    [InlineData("HslColor", "hsla(200,20%,30%,40%)", "AHSL")]
    [InlineData("HsvColor", "hsva(200,20%,30%,40%)", "AHSV")]
    public void SpanColorsMatchPublicParsersAndDoNotAllocateComponentStrings(string name, string sample, string properties)
    {
        var type = typeof(KnownFrameworkProfiles).Assembly.GetType("XamlG.Frameworks.Avalonia.Parsing." + name, true)!;
        typeof(SpanColorParserTests).GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, [name, sample, properties]);
    }

    private static void Check<T>(string name, string sample, string properties)
    {
        var parser = (TryColor<T>)typeof(T).GetMethod("TryParse", [typeof(ReadOnlySpan<char>), typeof(T).MakeByRefType()])!
            .CreateDelegate(typeof(TryColor<T>));
        var publicType = typeof(global::Avalonia.Media.Color).Assembly.GetType("Avalonia.Media." + name, true)!;
        var publicParser = publicType.GetMethod("TryParse", [typeof(string), publicType.MakeByRefType()])!;
        var samples = new List<string> { "", " ", "Red", "red", "#abcdef", "#abc", "#1234", "#aabbccdd", "#broken", sample };
        samples.AddRange(typeof(global::Avalonia.Media.Colors).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(property => new[] { property.Name, property.Name.ToLowerInvariant(), property.Name.ToUpperInvariant() }));
        foreach (var prefix in new[] { "rgb", "rgba", "hsl", "hsla", "hsv", "hsva", "HSL", "HSV", "RGB" })
        foreach (var components in new[] { "0,0,0", "0,0,0,1", "1,2", "1,2,3,4,5", "1,,3", "1,2,3,", ",1,2,3", "-1,256,3",
                     "1%,2%,3%", "1%ignored,2%,3%", " 1 , 2 , 3 , .5 ", "NaN,Infinity,-Infinity", "1e2,2,3", "1,2,3,50%extra" })
        foreach (var padding in new[] { "", " " })
            samples.Add(padding + prefix + "(" + components + ")" + padding);
        foreach (var text in samples)
        {
            object?[] arguments = [text, null];
            var expectedSuccess = (bool)publicParser.Invoke(null, arguments)!;
            var storage = "prefix" + text + "suffix";
            Assert.Equal(expectedSuccess, parser(storage.AsSpan(6, text.Length), out var actual));
            foreach (var property in properties.Select(value => value.ToString()))
            {
                var expectedValue = publicType.GetProperty(property)!.GetValue(arguments[1]);
                var actualValue = typeof(T).GetProperty(property)!.GetValue(actual);
                if (expectedValue is double expectedDouble)
                    Assert.Equal(BitConverter.DoubleToInt64Bits(expectedDouble), BitConverter.DoubleToInt64Bits((double)actualValue!));
                else Assert.Equal(expectedValue, actualValue);
            }
        }
        for (var i = 0; i < 1000; i++) parser(sample.AsSpan(), out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var succeeded = 0;
        for (var i = 0; i < 4096; i++) if (parser(sample.AsSpan(), out _)) succeeded++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4096, succeeded);
        Assert.Equal(0, allocated);
    }
}
