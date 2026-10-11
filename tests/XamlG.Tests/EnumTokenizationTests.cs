using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class EnumTokenizationTests
{
    private static readonly Func<string, INamedTypeSymbol, TextSpan, BoundExpression?> Bind = typeof(ValueBinder)
        .GetMethod("TryEnum", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<string, INamedTypeSymbol, TextSpan, BoundExpression?>>();

    [Theory]
    [InlineData("A")]
    [InlineData(" A ")]
    [InlineData("A,B")]
    [InlineData(" A , B ")]
    [InlineData("A,A,B")]
    [InlineData("A,,B")]
    [InlineData("A,")]
    [InlineData(",A")]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("\u00a0A\u2003,\tB\r\n")]
    public void Tokens_preserve_flags_trimming_empty_segments_duplicates_and_case(string text)
    {
        var compilation = CompilationFactory.Create("public enum Plain { A = 1, B = 2 } [System.Flags] public enum Flags { A = 1, B = 2 }");
        foreach (var name in new[] { "Plain", "Flags" })
        {
            var type = compilation.GetTypeByMetadataName(name)!;
            var expected = Previous(text, type, new(7, text.Length));
            var actual = Bind(text, type, new(7, text.Length));
            if (expected == null) { Assert.Null(actual); continue; }
            var fields = Assert.IsType<BoundEnumExpression>(actual);
            Assert.Equal(expected.Span, fields.Span);
            Assert.Same(type, fields.Type);
            Assert.Equal(expected.Fields.ToArray(), fields.Fields.ToArray());
        }
    }

    [Fact]
    public void Numeric_enum_spelling_keeps_the_current_culture_and_checked_safe_bit_patterns()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;
            var compilation = CompilationFactory.Create("public enum Tiny : byte { Zero = 0 } public enum Wide : ulong { Zero = 0 }");
            var tiny = Assert.IsType<BoundCastExpression>(Bind("~1", compilation.GetTypeByMetadataName("Tiny")!, new(1, 2)));
            Assert.Equal((byte)255, Assert.IsType<BoundConstantExpression>(tiny.Value).Value);
            var wide = Assert.IsType<BoundCastExpression>(Bind("~1", compilation.GetTypeByMetadataName("Wide")!, new(1, 2)));
            Assert.Equal(ulong.MaxValue, Assert.IsType<BoundConstantExpression>(wide.Value).Value);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static BoundEnumExpression? Previous(string text, INamedTypeSymbol type, TextSpan span)
    {
        var flags = type.GetAttributes().Any(attribute => attribute.AttributeClass?.Name == "FlagsAttribute");
        var fields = ImmutableArray.CreateBuilder<IFieldSymbol>();
        foreach (var name in flags ? text.Split(',').Select(value => value.Trim()) : new[] { text })
        {
            var field = type.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault(candidate => candidate.HasConstantValue);
            if (field == null) return null;
            fields.Add(field);
        }
        return fields.Count == 0 ? null : new(fields.ToImmutable(), type, span);
    }
}
