using System.Text.Json;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiExpressionQueryTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Run(string expression) => UiExpression.Parse(expression).Evaluate(J(new { threshold = 3 }), J(new
    {
        title = "  Avalonia  ", missing = (object?)null,
        rows = new[] { new { id = "a", score = 4 }, new { id = "b", score = 2 }, new { id = "c", score = 6 } }
    }));
    [Fact] public void FiltersProjectsAndSortsWithLexicalLambdaScope()
    {
        var value = Run("data.rows.Where(row => row.score > state.threshold).OrderByDescending(row => row.score).Select(row => new { row.id, amount = row.score * 2 }).ToArray()");
        Assert.Equal(2, value.GetArrayLength()); Assert.Equal("c", value[0].GetProperty("id").GetString()); Assert.Equal(12, value[0].GetProperty("amount").GetInt32());
    }
    [Theory]
    [InlineData("data.rows.Sum(row => row.score)", "12")]
    [InlineData("data.rows.Average(row => row.score)", "4")]
    [InlineData("data.rows.Count(row => row.score > 2)", "2")]
    [InlineData("data.rows.Min(row => row.score)", "2")]
    [InlineData("data.rows.Max(row => row.score)", "6")]
    [InlineData("data.rows.Aggregate(0, (sum, row) => sum + row.score)", "12")]
    [InlineData("data.rows.Any(row => row.score == 2)", "true")]
    [InlineData("data.rows.All(row => row.score > 0)", "true")]
    [InlineData("Math.Clamp(12, 0, 10)", "10")]
    [InlineData("Math.Round(2.345, 2)", "2.34")]
    [InlineData("data.title.Trim().Length", "8")]
    public void EvaluatesPureQueries(string expression, string expected) => Assert.Equal(expected, Run(expression).GetRawText());
    [Fact] public void SupportsNullConditionalFormattingCollectionsAndStrings()
    {
        Assert.Equal("none", Run("data.missing?.value ?? \"none\"").GetString());
        Assert.Equal("12.00", Run("$\"{data.rows.Sum(row => row.score):F2}\"").GetString());
        Assert.Equal("AVALONIA", Run("data.title.Trim().ToUpperInvariant()").GetString());
        Assert.Equal("a,c", Run("string.Join(\",\", data.rows.Where(row => row.score > 2).Select(row => row.id))").GetString());
        Assert.Equal(4, Run("[0, ..data.rows.Select(row => row.score)]").GetArrayLength());
        Assert.Equal(3, Run("Enumerable.Range(5, 3)").GetArrayLength());
        Assert.Equal(2, Run("new[] { 1, 2, 1 }.Distinct()").GetArrayLength());
        Assert.Equal("a", Run("data.rows.ToDictionary(row => row.id)[\"a\"].id").GetString());
    }
    [Theory]
    [InlineData("data.rows.ForEach(row => row.Delete())")]
    [InlineData("data.rows.Select(row => { return row; })")]
    [InlineData("data.rows.Select(data => data.score)")]
    [InlineData("data.title.GetType()")]
    [InlineData("Math.GetType()")]
    [InlineData("false ? System.IO.File.ReadAllText(\"x\") : \"ok\"")]
    [InlineData("$\"{1:F999999999}\"")]
    public void RejectsExecutionAndAllocationEscapesAtParse(string source) => Assert.Throws<UiException>(() => UiExpression.Parse(source));
    [Fact] public void BoundsExpansionReplacementAndDynamicFormats()
    {
        Assert.Throws<UiException>(() => Run("Enumerable.Range(0, 10000)"));
        Assert.Throws<UiException>(() => Run("data.title.PadLeft(100000000)"));
        Assert.Throws<UiException>(() => Run("(1).ToString(\"F999999999\")"));
        var expression = UiExpression.Parse("data.text.Replace(\"a\", data.text)", new(TextCharacters: 32));
        Assert.Throws<UiException>(() => expression.Evaluate(J(new { }), J(new { text = "aaaaaaaa" })));
    }
    [Fact] public void QueryErrorsDoNotPublishPartialState()
    {
        var store = new UiSessionStore();
        var request = UiExamples.Pricing();
        var first = store.Publish(request, "owner");
        Assert.Throws<UiException>(() => store.Publish(request with { ExpectedRevision = first.Revision, Sequence = 2, Xaml = "<TextBlock xmlns=\"https://github.com/avaloniaui\" Text=\"{ui:Expr string.Join(&quot;,&quot;, Enumerable.Range(0, 10000))}\"/>" }, "owner"));
        Assert.Same(first, store.Read(first.Id, "owner"));
    }
}
