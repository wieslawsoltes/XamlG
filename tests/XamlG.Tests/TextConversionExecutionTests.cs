using System.Collections;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class TextConversionExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_metadata_never_shares_runtime_conversion_results_or_skips_calls(bool sharedDocuments)
    {
        const string model = """
            using System;
            using System.Collections.Generic;
            using System.ComponentModel;
            using System.Globalization;
            using XamlG.Runtime;
            namespace ConversionExecution {
                public class Root { [Content] public List<Row> Children { get; } = new(); }
                public class Row { public Parsed P { get; set; } public Converted C { get; set; } public Bits F { get; set; } }
                public class Parsed {
                    public static int Calls;
                    public string Text { get; set; }
                    public static Parsed Parse(string text) => throw new Exception("wrong overload");
                    public static Parsed Parse(string text, IFormatProvider provider) {
                        Calls++; return new() { Text = (ReferenceEquals(provider, CultureInfo.InvariantCulture) ? "invariant:" : "wrong:") + text };
                    }
                }
                [TypeConverter(typeof(Converter))] public class Converted { public string Text { get; set; } }
                public class Converter : TypeConverter {
                    public static int Calls;
                    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value) {
                        Calls++; return new Converted { Text = "converted:" + value };
                    }
                }
                [Flags] public enum Bits { A = 1, B = 2 }
            }
            """;
        var xaml = "<Root xmlns='clr-namespace:ConversionExecution'>" +
            string.Concat(Enumerable.Range(0, 32).Select(i => "<Row P='v" + i + "' C='v" + i + "' F='A, B'/>")) + "</Root>";
        using var code = CompiledXaml.Create(xaml, model, shareAcrossDocuments: sharedDocuments);
        var parseCalls = code.Assembly.GetType("ConversionExecution.Parsed")!.GetField("Calls")!;
        var convertCalls = code.Assembly.GetType("ConversionExecution.Converter")!.GetField("Calls")!;
        Assert.Equal(0, parseCalls.GetValue(null)); Assert.Equal(0, convertCalls.GetValue(null));
        var observed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var build = 0; build < 2; build++)
        {
            var root = code.Build();
            try
            {
                var rows = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
                Assert.Equal(32, rows.Count);
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i]!;
                    var parsed = row.GetType().GetProperty("P")!.GetValue(row)!;
                    var converted = row.GetType().GetProperty("C")!.GetValue(row)!;
                    Assert.True(observed.Add(parsed)); Assert.True(observed.Add(converted));
                    Assert.Equal("invariant:v" + i, parsed.GetType().GetProperty("Text")!.GetValue(parsed));
                    Assert.Equal("converted:v" + i, converted.GetType().GetProperty("Text")!.GetValue(converted));
                    Assert.Equal(3, Convert.ToInt32(row.GetType().GetProperty("F")!.GetValue(row)));
                }
                Assert.Equal((build + 1) * 32, parseCalls.GetValue(null));
                Assert.Equal((build + 1) * 32, convertCalls.GetValue(null));
            }
            finally { if (XamlRuntimeSession.TryGet(root, out var session)) session!.Dispose(); }
        }
    }
}
