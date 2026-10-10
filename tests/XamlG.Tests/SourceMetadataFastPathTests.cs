using System.Globalization;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class SourceMetadataFastPathTests
{
    [Fact]
    public void Numeric_scanning_matches_the_previous_invariant_BCL_contract()
    {
        var samples = new List<string>
        {
            "", "0", "-0", "+0", "00", "-1", "+1", "2147483647", "2147483648", "-2147483648", "-2147483649",
            " 1", "1 ", "1\t", "\t1", "0\0", "1\0\0", "0\0x", "\0", "+", "-", "--0", "+-0", "0x1", "1e0",
            "\u0661", "\uff11", "\u22121", new string('0', 4096), new string('9', 4096)
        };
        var random = new Random(7319);
        const string alphabet = "0123456789+- \t\r\n\0x";
        for (var i = 0; i < 1000; i++)
            samples.Add(new string(Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        foreach (var spelling in samples)
        {
            var accepted = int.TryParse(spelling, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= 0;
            var table = new XamlSourceInfoTable("test.xaml", 7, new[] { spelling + ":0:-1:0:0:" });
            if (accepted)
            {
                Assert.Equal(value, table[0].Start);
                Assert.Equal(0, table[0].Length);
                Assert.Null(table[0].Identity);
            }
            else Assert.Throws<FormatException>(() => table[0]);
        }
    }

    [Fact]
    public void Record_framing_retains_unsigned_grammar_and_record_boundaries()
    {
        const string record = "0:0:-1:0:0:";
        var length = record.Length.ToString(CultureInfo.InvariantCulture);
        foreach (var prefix in new[] { length, "000" + length, length + "\0", "+" + length, "-" + length, " " + length, length + " ", "2147483648", "" })
        {
            var accepted = int.TryParse(prefix, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed == record.Length;
            if (accepted) Assert.Equal(0, XamlSourceInfoTable.FromEncoded("test", 0, prefix + ":" + record)[0].Start);
            else Assert.Throws<FormatException>(() => XamlSourceInfoTable.FromEncoded("test", 0, prefix + ":" + record));
        }
        var framed = record.Length + ":" + record;
        var table = XamlSourceInfoTable.FromEncoded("test", 0, framed + framed);
        Assert.Equal(0, table[1].Start);
        Assert.Throws<FormatException>(() => XamlSourceInfoTable.FromEncoded("test", 0, (record.Length - 1) + ":" + record));
    }

    [Fact]
    public void Decoded_metadata_is_published_once_and_remains_read_only()
    {
        const string record = "1:2:2:id4:hash1:3:key5:value";
        var table = XamlSourceInfoTable.FromEncoded("view.xaml", 9, record.Length + ":" + record);
        var values = new XamlSourceInfo[128];
        Parallel.For(0, values.Length, i => values[i] = table[0]);
        foreach (var value in values) Assert.Same(values[0], value);
        Assert.Equal("value", values[0].Declarations["key"]);
        Assert.Equal(9, values[0].Version);
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(values[0].Declarations);
        Assert.Throws<NotSupportedException>(() => dictionary["key"] = "changed");
    }

    [Fact]
    public void Public_metadata_constructor_still_copies_caller_owned_declarations()
    {
        var declarations = new Dictionary<string, string> { ["key"] = "original" };
        var value = new XamlSourceInfo("view", 1, 2, null, "hash", declarations);
        declarations["key"] = "mutated";
        declarations.Add("other", "new");
        Assert.Single(value.Declarations);
        Assert.Equal("original", value.Declarations["key"]);
    }
}
