using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpSourceActionTests
{
    [Theory]
    [InlineData("public int Number; public Probe(int n) { Number = n; } public static int Run() { Probe p = new Probe(42); return p.Number; }", "new Probe", "Use target-typed new", 42)]
    [InlineData("public int Number; public Probe(int n) { Number = n; } public static int Run() { Probe p = new(42); return p.Number; }", "new(42)", "Use explicit object creation", 42)]
    [InlineData("public int Number = 42; int Read() => Number; public static int Run() => new Probe().Read();", "Number;", "Qualify member with this", 42)]
    [InlineData("public static int Number = 42; public static int Run() => Number;", "Number;", "Qualify static member", 42)]
    [InlineData("public static int Run() { System.Int32 value = 42; return value; }", "Int32 value", "Use predefined type", 42)]
    [InlineData("public static string Run() => \"Run\";", "\"Run\"", "Use nameof(Run)", "Run")]
    [InlineData("public static int Run() => 42;", "Run()", "Use block body", 42)]
    [InlineData("public static int Run() { return 42; }", "Run()", "Use expression body", 42)]
    [InlineData("static int Value => 42; public static int Run() => Value;", "Value =>", "Use property block body", 42)]
    [InlineData("static int Value { get { return 42; } } public static int Run() => Value;", "Value {", "Use property expression body", 42)]
    [InlineData("static int Value = 41; static void Increment() => Value++; public static int Run() { Increment(); return Value; }", "Increment()", "Use block body", 42)]
    [InlineData("static int Fail() => throw new System.InvalidOperationException(\"expected\"); public static string Run() { try { Fail(); } catch (System.InvalidOperationException e) { return e.Message; } return \"miss\"; }", "Fail()", "Use block body", "expected")]
    [InlineData("int number; ref int Value => ref number; public static int Run() { var p = new Probe(); p.Value = 42; return p.number; }", "Value =>", "Use property block body", 42)]
    public void Semantic_actions_compile_and_preserve_observable_behavior(string body, string selection, string title, object expected)
    {
        var code = "public class Probe { " + body + " }";
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        Assert.Equal(expected, project.Run());
        var action = Assert.Single(project.Service.GetActions("Code.cs", code.IndexOf(selection, StringComparison.Ordinal)), action => action.Title == title);
        var updated = CSharpTestProject.Apply(code, action);
        Assert.NotEqual(code, updated);
        var result = new CSharpTestProject(("Code.cs", updated)); result.AssertCompiles();
        Assert.Equal(expected, result.Run());
    }

    [Fact]
    public void Type_qualification_preserves_cross_file_bindings_and_nullable_generic_arguments()
    {
        const string code = "#nullable enable\nusing Demo; public class Probe { public static string Run() { Box<string?> value = new(); return value.Read(\"kept\"); } }";
        const string model = "namespace Demo; public class Box<T> { public string Read(T value) => value?.ToString() ?? \"missing\"; }";
        var project = new CSharpTestProject(("Code.cs", code), ("Model.cs", model)); project.AssertCompiles();
        var action = Assert.Single(project.Service.GetActions("Code.cs", code.IndexOf("Box<", StringComparison.Ordinal)), action => action.Title == "Qualify type name");
        var updated = CSharpTestProject.Apply(code, action);
        Assert.Contains("global::Demo.Box<string?>", updated);
        var changed = new CSharpTestProject(("Code.cs", updated), ("Model.cs", model)); changed.AssertCompiles();
        Assert.Equal(project.Run(), changed.Run());
    }

    [Theory]
    [InlineData("public class Probe { static int Pick(object value) => 1; static int Pick(Probe value) => 2; public static int Run() => Pick(new object()); }", "new object", "Use target-typed new")]
    [InlineData("public class Probe { public static object Run() { var value = new Probe(); return value; } }", "new Probe", "Use target-typed new")]
    [InlineData("public class Probe { public static int Run() { /* preserve me */ return 42; } }", "Run()", "Use expression body")]
    [InlineData("public class Probe { public static int Run() => /* preserve me */ 42; }", "Run()", "Use block body")]
    [InlineData("public class Probe { public static object Run() { Probe value = new Probe /* preserve me */ (); return value; } }", "new Probe", "Use target-typed new")]
    [InlineData("public class Probe { public static string Run() => \"Unknown\"; }", "\"Unknown\"", "Use nameof(Unknown)")]
    public void Actions_do_not_change_overloads_lose_target_typing_or_discard_comments(string code, string selection, string title)
    {
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        Assert.DoesNotContain(project.Service.GetActions("Code.cs", code.IndexOf(selection, StringComparison.Ordinal)), action => action.Title == title);
    }

    [Fact]
    public void Rewriting_a_body_keeps_surrounding_trivia_and_CRLF_and_generated_files_remain_read_only()
    {
        const string code = "public class Probe\r\n{\r\n  // leading\r\n  public static int Run() => 42; // trailing\r\n}\r\n";
        var project = new CSharpTestProject(("Code.cs", code), ("Other.g.cs", "class Other { int Read() => 1; }"));
        var action = Assert.Single(project.Service.GetActions("Code.cs", code.IndexOf("Run()", StringComparison.Ordinal)), action => action.Title == "Use block body");
        var updated = CSharpTestProject.Apply(code, action);
        Assert.Contains("// leading\r\n  public static int Run()", updated);
        Assert.Contains("} // trailing\r\n", updated);
        Assert.DoesNotContain('\n', updated.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal(42, new CSharpTestProject(("Code.cs", updated)).Run());
        Assert.Empty(project.Service.GetActions("Other.g.cs", 18));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => project.Service.GetActions("Code.cs", code.IndexOf("Run()", StringComparison.Ordinal), cancellation.Token));
    }
}
