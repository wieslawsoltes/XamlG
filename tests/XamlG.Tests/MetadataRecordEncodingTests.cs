using System.Globalization;
using System.Text;
using XamlG.CSharp;
using Xunit;

namespace XamlG.Tests;

public sealed class MetadataRecordEncodingTests
{
    [Fact]
    public void EveryIntegerBoundaryAndRandomValueMatchesInvariantFraming()
    {
        var samples = new List<int> { int.MinValue, int.MaxValue, -1, 0, 1 };
        for (long power = 10; power <= 1_000_000_000; power *= 10)
            for (var delta = -1; delta <= 1; delta++)
            { samples.Add((int)power + delta); samples.Add(-(int)power + delta); }
        var random = new Random(92741);
        for (var i = 0; i < 10000; i++) samples.Add((int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
        var actual = new StringBuilder();
        foreach (var value in samples)
        {
            actual.Clear();
            MetadataRecordEncoding.AppendNumber(actual, value);
            Assert.Equal(value.ToString(CultureInfo.InvariantCulture) + ":", actual.ToString());
        }
    }

    [Fact]
    public void TextLengthsCountUtf16CodeUnitsAndDistinguishNullFromEmpty()
    {
        var actual = new StringBuilder();
        foreach (var value in new string?[] { null, "", "a:b", "名😀", "\ud800", "\0\r\n" })
        {
            actual.Clear();
            MetadataRecordEncoding.AppendText(actual, value);
            Assert.Equal((value?.Length ?? -1).ToString(CultureInfo.InvariantCulture) + ":" + value, actual.ToString());
        }
    }

    [Fact]
    public void AmbientCultureCannotAlterSignedFields()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            custom.NumberFormat.NegativeSign = "not-a-minus";
            CultureInfo.CurrentCulture = custom;
            var actual = new StringBuilder();
            MetadataRecordEncoding.AppendNumber(actual, int.MinValue);
            MetadataRecordEncoding.AppendText(actual, null);
            Assert.Equal("-2147483648:-1:", actual.ToString());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
