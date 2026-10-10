using System.Collections;
using XamlG.Compiler;
using XamlG.Runtime;
using Xunit;

namespace XamlG.Tests;

public sealed class WideEmissionTests
{
    [Theory]
    [InlineData(8, false, false)]
    [InlineData(9, true, false)]
    [InlineData(128, false, false)]
    [InlineData(128, true, false)]
    [InlineData(128, false, true)]
    [InlineData(128, true, true)]
    public void Wide_leaf_and_markup_helpers_preserve_values_and_source_ranges(int width, bool directives, bool project)
    {
        var properties = string.Concat(Enumerable.Range(0, width).Select(i => "public string P" + i + " { get; set; } = \"\";"));
        var model = "using System.Collections.Generic; using XamlG.Runtime; namespace Wide { " +
            "public class Root { [Content] public List<object> Children { get; } = new(); } " +
            "public class Holder { public object Value { get; set; } } " +
            "public class Leaf { " + properties + " } public class WideExtension { " + properties +
            " public object ProvideValue() => this; } }";
        var leaf = "<Leaf " + string.Join(" ", Enumerable.Range(0, width).Select(i => "P" + i + "='value_" + i + "'")) + "/>";
        var markup = "<Holder Value='{Wide " + string.Join(", ", Enumerable.Range(0, width).Select(i => "P" + i + "=value_" + i)) + "}'/>";
        var xaml = "<Root xmlns='clr-namespace:Wide'>" + leaf + leaf + markup + markup + "</Root>";
        using var code = CompiledXaml.Create(xaml, model, shareAcrossDocuments: project, options: new() { EmitLineDirectives = directives });
        Assert.Contains("__XamlGCreateLeaf_", code.Emission.Source, StringComparison.Ordinal);
        Assert.Contains("__XamlGAssignMarkup_", code.Emission.Source, StringComparison.Ordinal);
        var root = code.Build();
        Assert.True(XamlRuntimeSession.TryGet(root, out var session));
        using (session)
        {
            var children = (IList)root.GetType().GetProperty("Children")!.GetValue(root)!;
            Assert.Equal(4, children.Count);
            Assert.NotSame(children[0], children[1]);
            for (var child = 0; child < children.Count; child++)
            {
                var value = children[child]!;
                if (child >= 2) value = value.GetType().GetProperty("Value")!.GetValue(value)!;
                Assert.NotNull(session!.FindNode(value));
                for (var i = 0; i < width; i++) Assert.Equal("value_" + i, value.GetType().GetProperty("P" + i)!.GetValue(value));
            }
        }
        foreach (var mapping in code.Emission.SourceMappings)
        {
            Assert.InRange(mapping.GeneratedSpan.End, 0, code.Emission.Source.Length);
            Assert.InRange(mapping.SourceSpan.End, 0, xaml.Length);
        }
        for (var i = 0; i < width; i++)
        {
            var literal = "value_" + i;
            Assert.Contains(code.Emission.SourceMappings, mapping =>
                code.Emission.Source.Substring(mapping.GeneratedSpan.Start, mapping.GeneratedSpan.Length) == "\"" + literal + "\"" &&
                xaml.Substring(mapping.SourceSpan.Start, mapping.SourceSpan.Length) == literal);
        }
    }
}
