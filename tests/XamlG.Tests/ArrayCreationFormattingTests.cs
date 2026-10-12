using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Roslyn;
using Xunit;

namespace XamlG.Tests;

public sealed class ArrayCreationFormattingTests
{
    private static readonly Func<BoundArrayExpression, string> Format =
        (Func<BoundArrayExpression, string>)typeof(ValueEmitter).GetMethod("ArrayCreation", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate(typeof(Func<BoundArrayExpression, string>));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(32)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Deep_and_mixed_rank_shapes_match_the_frozen_formatter(int depth)
    {
        var compilation = CompilationFactory.Create("public class Placeholder { }");
        foreach (var mixed in new[] { false, true })
        {
            var valueType = compilation.GetSpecialType(SpecialType.System_Object);
            ITypeSymbol element = valueType;
            for (var i = 0; i < depth; i++) element = compilation.CreateArrayTypeSymbol(element, mixed ? i % 4 + 1 : 1);
            var array = compilation.CreateArrayTypeSymbol(element);
            foreach (var count in new[] { 0, 3 })
            {
                var expression = new BoundArrayExpression(System.Collections.Immutable.ImmutableArray.CreateRange(
                    Enumerable.Repeat<BoundExpression>(new BoundConstantExpression(null, valueType, default), count)), array, default);
                Assert.Equal(Previous(expression), Format(expression));
            }
        }
    }

    [Fact]
    public void Mixed_rank_creation_forms_compile_without_changing_type_order()
    {
        var compilation = CompilationFactory.Create("public class Placeholder { }");
        ITypeSymbol element = compilation.GetSpecialType(SpecialType.System_Object);
        for (var depth = 0; depth < 8; depth++)
        {
            var array = compilation.CreateArrayTypeSymbol(element);
            var creation = new BoundArrayExpression([], array, default);
            var source = "public static class Factory" + depth + " { public static " + array.CSharpName() +
                " Create() => " + Format(creation) + "; }";
            using var output = new MemoryStream();
            var result = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)compilation.SyntaxTrees.First().Options)).Emit(output);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            element = compilation.CreateArrayTypeSymbol(element, depth % 3 + 1);
        }
    }

    [Fact]
    public void Parallel_formatting_never_shares_mutable_builder_state()
    {
        var compilation = CompilationFactory.Create("public class Placeholder { }");
        ITypeSymbol element = compilation.GetSpecialType(SpecialType.System_Object);
        for (var i = 0; i < 128; i++) element = compilation.CreateArrayTypeSymbol(element, i % 4 + 1);
        var expression = new BoundArrayExpression([], compilation.CreateArrayTypeSymbol(element), default);
        var expected = Previous(expression);
        Parallel.For(0, 128, _ => Assert.Equal(expected, Format(expression)));
    }

    private static string Previous(BoundArrayExpression array)
    {
        var element = array.ArrayType.ElementType;
        var suffix = string.Empty;
        while (element is IArrayTypeSymbol nested)
        { suffix += "[" + new string(',', nested.Rank - 1) + "]"; element = nested.ElementType; }
        return "new " + element.CSharpName() + "[" + array.Values.Length + "]" + suffix;
    }
}
