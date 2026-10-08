using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class SourceInfoTableTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordsPreserveMetadataAndCacheImmutableValuesAcrossThreads(bool encoded)
    {
        var records = new[] { "12:30:6:名:😀\n:4:hash2:4:Text5:a:b:c0:0:", "0:0:-1:0:0:" };
        var table = encoded ? XamlSourceInfoTable.FromEncoded("😀:View.xaml", 42, Encode(records)) : new XamlSourceInfoTable("😀:View.xaml", 42, records);
        records[0] = "mutated";
        var values = new XamlSourceInfo[64];
        Parallel.For(0, values.Length, i => values[i] = table[0]);
        var value = values[0];
        Assert.All(values, item => Assert.Same(value, item));
        Assert.Equal("😀:View.xaml", value.Path);
        Assert.Equal(12, value.Start); Assert.Equal(30, value.Length); Assert.Equal(42, value.Version);
        Assert.Equal("名:😀\n:", value.Identity); Assert.Equal("hash", value.Fingerprint);
        Assert.Equal("a:b:c", value.Declarations["Text"]); Assert.Equal("", value.Declarations[""]);
        Assert.Null(table[1].Identity); Assert.Equal("", table[1].Fingerprint); Assert.Empty(table[1].Declarations);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)value.Declarations).Add("x", "y"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1:0:-1:0:0:")]
    [InlineData("2147483647:1:-1:0:0:")]
    [InlineData("0:0:-2:0:0:")]
    [InlineData("0:0:99:x")]
    [InlineData("0:0:-1:-1:0:")]
    [InlineData("0:0:-1:0:-1:")]
    [InlineData("0:0:-1:0:2147483647:")]
    [InlineData("0:0:-1:0:1:0:5:x")]
    [InlineData("0:0:-1:0:2:0:0:0:0:")]
    [InlineData("0:0:-1:0:0:trailing")]
    public void MalformedRecordsFailOnlyWhenAccessed(string record)
    {
        var records = new[] { "0:0:-1:0:0:", record };
        foreach (var table in new[] { new XamlSourceInfoTable("View.xaml", 0, records), XamlSourceInfoTable.FromEncoded("View.xaml", 0, Encode(records)) })
        {
            Assert.NotNull(table[0]);
            Assert.Throws<FormatException>(() => table[1]);
        }
    }

    [Theory]
    [InlineData("x")]
    [InlineData(":")]
    [InlineData("-1:")]
    [InlineData("+1:x")]
    [InlineData(" 1:x")]
    [InlineData("2:x")]
    [InlineData("2147483648:")]
    [InlineData("0:trailing")]
    public void MalformedRecordBoundariesAreRejected(string records) =>
        Assert.Throws<FormatException>(() => XamlSourceInfoTable.FromEncoded("View.xaml", 0, records));

    [Fact]
    public void DecodingCannotReadPastItsOwnRecordBoundary()
    {
        var table = XamlSourceInfoTable.FromEncoded("View.xaml", 0, Encode(new[] { "0:", "0:0:-1:0:0:" }));
        Assert.Throws<FormatException>(() => table[0]);
        Assert.NotNull(table[1]);
        Assert.Throws<IndexOutOfRangeException>(() => table[-1]);
        Assert.Throws<IndexOutOfRangeException>(() => table[2]);
        Assert.Throws<IndexOutOfRangeException>(() => XamlSourceInfoTable.FromEncoded("View.xaml", 0, "")[0]);
    }

    private static string Encode(string[] records) => string.Concat(records.Select(record =>
        record.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + record));
}
