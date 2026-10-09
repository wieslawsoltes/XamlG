using System.Text.Json;
using Microsoft.CodeAnalysis;
using Xunit;

namespace XamlG.IntelligentUI.Tests;

public sealed class UiFullCSharpTests
{
    private static MetadataReference[] References() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(IUiExpression).Assembly.Location).Distinct(StringComparer.Ordinal).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray();
    [Fact] public void FullCSharpRequiresExactOwnerApprovalIncludingCachedSources()
    {
        var approved = false; var reviewed = new List<string>();
        using var compiler = new UiCSharpExpressionCompiler(References(), request => { reviewed.Add(request.Sha256); return approved; });
        const string source = "{ int Add(int a, int b) => a + b; return Add(2, 3); }";
        Assert.Throws<UiException>(() => compiler.Compile(source, new())); approved = true;
        var expression = compiler.Compile(source, new());
        Assert.Equal(5, expression.Evaluate(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { })).GetInt32());
        approved = false; Assert.Throws<UiException>(() => compiler.Compile(source, new()));
        Assert.Single(reviewed.Distinct()); Assert.Equal(64, reviewed[0].Length);
    }
    [Fact] public void ExecutesRealCSharpPatternsLocalFunctionsAndFrameworkLinq()
    {
        using var compiler = new UiCSharpExpressionCompiler(References(), _ => true);
        var expression = compiler.Compile("{ var total = 0; foreach (var itemValue in data.GetProperty(\"rows\").EnumerateArray()) { total += itemValue.GetInt32() switch { > 3 and < 10 => itemValue.GetInt32(), _ => 0 }; } return new { Total = total, Kind = typeof(int).Name, Values = Enumerable.Range(1, 3).Select(x => x * x).ToArray() }; }", new());
        var result = expression.Evaluate(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { rows = new[] { 2, 5, 8, 20 } }));
        Assert.Equal(13, result.GetProperty("Total").GetInt32()); Assert.Equal("Int32", result.GetProperty("Kind").GetString()); Assert.Equal(9, result.GetProperty("Values")[2].GetInt32());
    }
    [Fact] public void FullBackendWorksThroughTheSameXamlTemplateAndRejectsCompileErrors()
    {
        using var expressions = new UiCSharpExpressionCompiler(References(), _ => true);
        var store = new UiSessionStore(new(expressionCompiler: expressions));
        var source = "<TextBlock xmlns=\"https://github.com/avaloniaui\" Text=\"{ui:Expr { return string.Join(&quot;,&quot;, Enumerable.Range(1, 3)); }}\"/>";
        var snapshot = store.Publish(new("full", 0, 1, source), "owner"); Assert.Equal("1,2,3", snapshot.Roots[0].Properties["Text"].GetString());
        Assert.Throws<UiException>(() => expressions.Compile("no_such_method()", new()));
        Assert.Throws<UiException>(() => new UiSessionStore().RestoreArchive(store.CaptureArchive("full-workspace"), "full-workspace"));
    }
    [Fact] public void CompilationAndResultLimitsAreIndependentOfExecutionTrust()
    {
        using var compiler = new UiCSharpExpressionCompiler(References(), _ => true, maximumCompilations: 1);
        var expression = compiler.Compile("new string('x', 100)", new(DataBytes: 16));
        Assert.Throws<UiException>(() => expression.Evaluate(JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { })));
        Assert.Throws<UiException>(() => compiler.Compile("2 + 2", new()));
    }
}
