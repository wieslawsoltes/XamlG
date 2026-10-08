using System.Text.Json;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpInspectionTests
{
    private const string OperationsSource = """
        using System;
        public class Sample
        {
            public int Field = 3;
            public int Computed { get; } = 4;
            public Sample() { Field++; }
            public double Read(int x) => Math.Abs(x) + 0.5;
            public int? Lift(int? x) => x + 1;
        }
        """;

    [Fact]
    public void Operations_cover_initializers_constructors_conversions_calls_and_lifted_operators()
    {
        var project = new CSharpTestProject(("Code.cs", OperationsSource)); project.AssertCompiles();
        var result = project.Service.GetOperations("Code.cs");
        Assert.False(result.Truncated); Assert.False(result.IsGenerated);
        Assert.Equal(OperationsSource.Length, result.Selection.Length);
        Assert.Contains(result.Nodes, node => node.Kind == "FieldInitializer");
        Assert.Contains(result.Nodes, node => node.Kind == "PropertyInitializer");
        Assert.Contains(result.Nodes, node => node.Kind == "Increment");
        Assert.Contains(result.Nodes, node => node.Kind == "Invocation" && node.Symbol == "System.Math.Abs(int)");
        Assert.Contains(result.Nodes, node => node.Kind == "Conversion" && node.Type == "double" && node.IsImplicit);
        Assert.Contains(result.Nodes, node => node.Kind == "Binary" && node.Details.TryGetValue("isLifted", out var lifted) && lifted is true);
        Assert.Contains(result.Nodes, node => node.HasConstant && Equals(node.Constant, 3));
        Assert.Equal(Enumerable.Range(0, result.Nodes.Length), result.Nodes.Select(node => node.Id));
        foreach (var node in result.Nodes)
        {
            Assert.Equal(node.ChildCount, node.Children.Length);
            Assert.False(node.ChildrenTruncated);
            foreach (var child in node.Children)
            {
                Assert.Equal(node.Id, result.Nodes[child].ParentId);
                Assert.Equal(node.Depth + 1, result.Nodes[child].Depth);
            }
            Assert.Equal(OperationsSource.Substring(node.Span.Start, Math.Min(256, node.Span.Length)), node.Syntax);
        }
        Assert.All(result.Roots, id => Assert.Null(result.Nodes[id].ParentId));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(result.Nodes.Length, json.RootElement.GetProperty("Nodes").GetArrayLength());
    }

    [Fact]
    public void Operation_selection_and_limits_report_the_actual_selected_expression_and_omitted_children()
    {
        var service = new CSharpTestProject(("Code.cs", OperationsSource)).Service;
        const string expression = "Math.Abs(x)";
        var offset = OperationsSource.IndexOf(expression, StringComparison.Ordinal);
        var selected = service.GetOperations("Code.cs", offset, expression.Length);
        Assert.Equal("Invocation", selected.Nodes[Assert.Single(selected.Roots)].Kind);
        Assert.Equal(offset, selected.Selection.Start); Assert.Equal(expression.Length, selected.Selection.Length);
        var shallow = service.GetOperations("Code.cs", maximumDepth: 0);
        Assert.True(shallow.Truncated);
        Assert.All(shallow.Nodes, node => { Assert.Equal(0, node.Depth); Assert.Empty(node.Children); });
        Assert.Contains(shallow.Nodes, node => node.ChildCount > 0 && node.ChildrenTruncated);
        var limited = service.GetOperations("Code.cs", maximumNodes: 4);
        Assert.True(limited.Truncated); Assert.Equal(4, limited.Nodes.Length);
        Assert.All(limited.Nodes.SelectMany(node => node.Children), id => Assert.InRange(id, 0, 3));
    }

    [Fact]
    public void Control_flow_preserves_branch_edges_finally_regions_and_capture_identities()
    {
        const string code = """
            using System;
            public class Probe
            {
                public static string Read(string? value, bool enabled)
                {
                    string result;
                    try { result = enabled ? (value ?? "empty") : "off"; }
                    finally { Console.WriteLine("done"); }
                    return result;
                }
            }
            """;
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var result = project.Service.GetControlFlow("Code.cs", code.IndexOf("Read(", StringComparison.Ordinal));
        Assert.Null(result.ParentBody); Assert.False(result.Truncated);
        Assert.Equal(result.TotalBlocks, result.Blocks.Length); Assert.Equal(result.TotalRegions, result.Regions.Length);
        Assert.Equal("Entry", result.Blocks[0].Kind); Assert.Equal("Exit", result.Blocks[^1].Kind);
        Assert.Contains(result.Regions, region => region.Kind == "Finally");
        Assert.Contains(result.Symbols, symbol => symbol.Name == "result" && symbol.Type == "string");
        var edges = result.Blocks.SelectMany(block => new[] { block.Conditional, block.FallThrough }).OfType<CSharpFlowBranch>().ToArray();
        Assert.Contains(edges, edge => !edge.FinallyRegions.IsEmpty);
        Assert.Contains(edges, edge => edge.Semantics == "Return");
        Assert.Contains(result.Blocks, block => block.Conditional != null && block.BranchValue != null);
        foreach (var block in result.Blocks)
        {
            Assert.Contains(result.Regions, region => region.Id == block.RegionId);
            foreach (var destination in new[] { block.Conditional?.Destination, block.FallThrough?.Destination }.OfType<int>())
                Assert.Contains(block.Ordinal, result.Blocks[destination].Predecessors);
        }
        var captures = result.Regions.SelectMany(region => region.Captures).ToHashSet();
        Assert.NotEmpty(captures);
        foreach (var operation in result.Operations.Where(operation => operation.Details.ContainsKey("captureId")))
            Assert.Contains(Assert.IsType<int>(operation.Details["captureId"]), captures);
        Assert.All(edges.SelectMany(edge => edge.LeavingRegions.Concat(edge.EnteringRegions).Concat(edge.FinallyRegions)),
            id => Assert.Contains(result.Regions, region => region.Id == id));
    }

    [Fact]
    public void Control_flow_resolves_a_lambda_inside_a_local_function_to_its_own_body()
    {
        const string code = """
            using System;
            public class Probe
            {
                public static int Run(int seed)
                {
                    int Local(int delta)
                    {
                        Func<int, int> map = value => seed + value + delta;
                        return map(2);
                    }
                    return Local(3);
                }
            }
            """;
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var lambda = project.Service.GetControlFlow("Code.cs", code.IndexOf("seed +", StringComparison.Ordinal));
        Assert.NotNull(lambda.ParentBody);
        Assert.Contains("seed + value + delta", Slice(lambda.Body));
        Assert.DoesNotContain("return map", Slice(lambda.Body));
        Assert.Contains("return map(2)", Slice(lambda.ParentBody!));
        Assert.DoesNotContain("return Local(3)", Slice(lambda.ParentBody!));
        var local = project.Service.GetControlFlow("Code.cs", code.IndexOf("Local(int", StringComparison.Ordinal));
        Assert.Equal(lambda.ParentBody, local.Body);
        Assert.Contains("return Local(3)", Slice(local.ParentBody!));
        Assert.False(local.Truncated); Assert.False(lambda.Truncated);
        Assert.Null(lambda.Regions[0].ParentId); Assert.Null(local.Regions[0].ParentId);
        Assert.All(lambda.Regions.Where(region => region.ParentId != null), region =>
            Assert.Contains(lambda.Regions, parent => parent.Id == region.ParentId));
        Assert.Contains(lambda.Operations, operation => operation.Symbol == "int value");
        string Slice(CSharpLocation location) => code.Substring(location.Start, location.Length);
    }

    [Fact]
    public void Control_flow_resolves_a_local_function_inside_a_lambda()
    {
        const string code = "using System; class Probe { int Run(int x) { Func<int> f = () => { int Local() => x + 1; return Local(); }; return f(); } }";
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var result = project.Service.GetControlFlow("Code.cs", code.IndexOf("x + 1", StringComparison.Ordinal));
        Assert.Contains("Local() => x + 1", code.Substring(result.Body.Start, result.Body.Length));
        Assert.NotNull(result.ParentBody);
        Assert.Contains("return Local()", code.Substring(result.ParentBody!.Start, result.ParentBody.Length));
        Assert.DoesNotContain("return f()", code.Substring(result.ParentBody.Start, result.ParentBody.Length));
    }

    [Fact]
    public void Data_flow_reports_inputs_outputs_declarations_and_UTF16_statement_ranges()
    {
        const string code = "class Probe { int Run(int input) { /* 😀 */ int result = input; result += 2; return result; } }";
        const string range = "int result = input; result += 2;";
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var offset = code.IndexOf(range, StringComparison.Ordinal);
        var result = project.Service.GetDataFlow("Code.cs", offset, range.Length);
        Assert.True(result.Succeeded); Assert.False(result.Truncated); Assert.Equal("StatementRange", result.RegionKind);
        Assert.Equal(offset, result.Region.Start); Assert.Equal(range.Length, result.Region.Length);
        Assert.Equal(offset, result.Region.StartColumn);
        Assert.Equal(["result"], Names(result, "variablesDeclared"));
        Assert.Equal(["input"], Names(result, "dataFlowsIn"));
        Assert.Equal(["result"], Names(result, "dataFlowsOut"));
        Assert.Contains("result", Names(result, "alwaysAssigned"));
        Assert.Contains("input", Names(result, "readInside"));
        Assert.Contains("result", Names(result, "writtenInside"));
        foreach (var symbol in result.Symbols.Where(symbol => !symbol.IsImplicit))
            Assert.All(symbol.Locations, location => Assert.Equal(symbol.Name, code.Substring(location.Start, location.Length)));
    }

    [Fact]
    public void Data_flow_tracks_captures_and_local_function_use_and_marks_symbol_truncation()
    {
        const string code = "using System; class Probe { int Run(int seed) { int delta = 1; Func<int> capture = () => seed + delta; int Local() => capture(); return Local(); } }";
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var offset = code.IndexOf("Run(", StringComparison.Ordinal);
        var result = project.Service.GetDataFlow("Code.cs", offset);
        Assert.True(result.Succeeded); Assert.False(result.Truncated);
        Assert.Contains("seed", Names(result, "captured")); Assert.Contains("delta", Names(result, "captured"));
        Assert.Contains("Local", Names(result, "usedLocalFunctions"));
        var limited = project.Service.GetDataFlow("Code.cs", offset, maximumSymbols: 1);
        Assert.True(limited.Truncated); Assert.Single(limited.Symbols);
        Assert.Contains(limited.Sets.Values, set => set.Truncated && set.TotalCount > set.Symbols.Length);
        Assert.All(limited.Sets.Values.SelectMany(set => set.Symbols), id => Assert.Equal(0, id));
    }

    [Fact]
    public void Generated_inspection_keeps_provenance_and_flow_limits_remain_serializable()
    {
        const string code = "partial class Probe { int Generated(int a) { int x = a > 0 ? a : -a; return x + 1; } }";
        var project = new CSharpTestProject(("Code.cs", "partial class Probe {}"), ("Probe.g.cs", code)); project.AssertCompiles();
        var offset = code.IndexOf("Generated", StringComparison.Ordinal);
        Assert.True(project.Service.GetOperations("Probe.g.cs").IsGenerated);
        var graph = project.Service.GetControlFlow("Probe.g.cs", offset);
        Assert.True(graph.Body.IsGenerated);
        var flow = project.Service.GetDataFlow("Probe.g.cs", offset);
        Assert.True(flow.Region.IsGenerated);
        Assert.All(flow.Symbols.SelectMany(symbol => symbol.Locations), location => Assert.True(location.IsGenerated));
        var limited = project.Service.GetControlFlow("Probe.g.cs", offset, maximumBlocks: 2, maximumNodes: 1, maximumDepth: 0, maximumRegions: 1, maximumSymbols: 1);
        Assert.True(limited.Truncated); Assert.Equal(2, limited.Blocks.Length); Assert.Single(limited.Regions);
        Assert.InRange(limited.Operations.Length, 0, 1); Assert.InRange(limited.Symbols.Length, 0, 1);
        Assert.True(limited.TotalBlocks > limited.Blocks.Length); Assert.True(limited.TotalRegions > limited.Regions.Length);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(limited));
        Assert.True(json.RootElement.GetProperty("Truncated").GetBoolean());
    }

    [Fact]
    public void Invalid_selections_bounds_and_cancellation_do_not_produce_misleading_flow_results()
    {
        var service = new CSharpTestProject(("Code.cs", "abstract class Probe { public abstract int Run(); }"), ("Empty.cs", "")).Service;
        Assert.Empty(service.GetOperations("Empty.cs").Nodes);
        Assert.Empty(service.GetOperations("Code.cs").Nodes);
        Assert.Throws<ArgumentException>(() => service.GetControlFlow("Code.cs", 15));
        Assert.Throws<ArgumentException>(() => service.GetDataFlow("Code.cs", 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetOperations("Code.cs", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetOperations("Code.cs", maximumDepth: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetControlFlow("Code.cs", 0, maximumBlocks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetDataFlow("Code.cs", 1, int.MaxValue));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.GetOperations("Code.cs", cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetControlFlow("Code.cs", 15, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetDataFlow("Code.cs", 15, cancellationToken: cancellation.Token));
    }

    private static string[] Names(CSharpDataFlowResult result, string set) => result.Sets[set].Symbols.Select(id => result.Symbols[id].Name).Order(StringComparer.Ordinal).ToArray();
}
