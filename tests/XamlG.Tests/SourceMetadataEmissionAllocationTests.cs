using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class SourceMetadataEmissionAllocationTests
{
    [Fact]
    public void StableIdsPreserveTheExactUtf8Sha256PrefixAndLowercaseEncoding()
    {
        foreach (var value in new[] { "", "ASCII", "a\0b", "名:😀\r\n", new string('x', 8193), "\ud800", "\udfff" })
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            var expected = Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
            Assert.Equal(expected, CSharpNames.StableId(value));
            using var hash = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(value + "trailing bytes must not be hashed");
            Assert.Equal(expected, CSharpNames.StableId(hash, bytes, Encoding.UTF8.GetByteCount(value)));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(256, false)]
    public void EmptySingletonDuplicateAndWideMetadataRemainByteIdentical(int count, bool duplicate)
    {
        var f = new EmissionAnalysisFixture();
        var text = "<Node xmlns='clr-namespace:Traversal'><!-- 名:😀 -->" + new string(' ', 4096) + "</Node>";
        var syntax = XamlSyntaxTree.Parse(text, "名/View.xaml");
        var document = f.Document with { Syntax = syntax };
        var assignments = new List<BoundAssignment>();
        for (var i = 0; i < count; i++)
        {
            var name = duplicate ? "Same" : "Property" + (count - i).ToString(CultureInfo.InvariantCulture);
            assignments.Add(new BoundSetAssignment(f.Member with { Name = name },
                new BoundConstantExpression(null, f.Root.Type, default), new(i * 5, i % 7)));
        }
        // Non-property operations must not allocate/emit a declaration record.
        assignments.Add(new BoundRawAssignment("", default));
        var value = f.Root with { Name = "named", Key = "key:名", Assignments = [.. assignments] };
        using var expected = new EmissionContext(document, default);
        using var actual = new EmissionContext(document, default);
        var emitter = new SourceInfoEmitter(actual);
        Assert.Equal(PreviousIndex(expected, value), emitter.Index(value));
        Assert.Equal(PreviousIndex(expected, value), emitter.Index(value));
        var leaf = value with { Name = null, Assignments = [] };
        Assert.Equal(PreviousIndex(expected, leaf), emitter.Index(leaf));
        expected.EmitMetadataHelpers(); actual.EmitMetadataHelpers();
        Assert.Equal(expected.Writer.ToString(), actual.Writer.ToString());
    }

    private static int PreviousIndex(EmissionContext context, BoundObject value)
    {
        var syntax = context.Document.Syntax;
        var span = Clamp(value.Syntax.Span, syntax.Text.Length);
        var fingerprint = context.StableId(value.Type.CSharpName(), syntax.Text, span);
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in value.Assignments)
        {
            var member = assignment switch
            {
                BoundSetAssignment set => set.Member.Name,
                BoundAdaptedSetAssignment set => set.Member.Name,
                BoundDynamicSetAssignment set => set.Target.Name,
                _ => null
            };
            if (member == null) continue;
            var source = Clamp(assignment.Span, syntax.Text.Length);
            var digest = context.StableId(syntax.Text, source);
            declarations[member] = declarations.TryGetValue(member, out var previous) ? context.StableId(previous + digest) : digest;
        }
        var record = new StringBuilder();
        void Number(int number) => record.Append(number.ToString(CultureInfo.InvariantCulture)).Append(':');
        void Text(string? text) { Number(text?.Length ?? -1); record.Append(text); }
        Number(span.Start); Number(span.Length);
        Text(value.Name == null ? null : value.Key); Text(fingerprint); Number(declarations.Count);
        foreach (var pair in declarations.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(pair.Key); Text(pair.Value); }
        return context.SourceInfoIndex(record.ToString());
    }

    private static TextSpan Clamp(TextSpan span, int length)
    {
        var start = Math.Min(span.Start, length);
        return new(start, Math.Min(span.Length, length - start));
    }
}
