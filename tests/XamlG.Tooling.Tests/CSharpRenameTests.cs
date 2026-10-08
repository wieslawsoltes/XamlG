using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Tooling.Refactoring;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpRenameTests
{
    private static CSharpRenameService Service(params (string Path, string Text)[] files)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        return new(CSharpCompilation.Create("Rename", files.Select(f => CSharpSyntaxTree.ParseText(f.Text, path: f.Path)), references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)), files.Where(f => !f.Path.EndsWith(".g.cs", StringComparison.Ordinal)).Select(f => f.Path));
    }
    [Fact]
    public void Cross_file_rename_preserves_unrelated_locals_strings_and_overloads()
    {
        const string definition = "public class State { public int Value; public int Read(int value) => value; public int Read(string value) => value.Length; }";
        const string use = "class View { int Get(State state) => state.Value + state.Read(2); string Other() { var Value = \"Value\"; return Value; } }";
        var plan = Service(("State.cs", definition), ("View.cs", use)).Rename("State.cs", definition.IndexOf("Value", StringComparison.Ordinal), "Number");
        Assert.Equal(2, plan.Documents.Length); Assert.All(plan.Documents, d => Assert.Single(d.Changes));
        var method = Service(("State.cs", definition), ("View.cs", use)).Rename("State.cs", definition.IndexOf("Read(int", StringComparison.Ordinal), "ReadNumber");
        Assert.Equal(2, method.Documents.Length); Assert.All(method.Documents, d => Assert.Single(d.Changes));
    }
    [Fact]
    public void Silent_capture_of_an_existing_field_is_rejected()
    {
        const string code = "class State { int count; int Use() { var value = 1; return value + count; } }";
        var error = Assert.Throws<InvalidOperationException>(() => Service(("Code.cs", code)).Rename("Code.cs", code.IndexOf("value", StringComparison.Ordinal), "count"));
        Assert.Contains("binding", error.Message);
    }
    [Fact]
    public void Type_rename_includes_constructor_and_references_but_not_inferred_var()
    {
        const string code = "class State { public State() {} } class View { State Get() { var value = new State(); return value; } }";
        var plan = Service(("Code.cs", code)).Rename("Code.cs", code.IndexOf("State", StringComparison.Ordinal), "Model");
        var changes = Assert.Single(plan.Documents).Changes;
        Assert.Equal(4, changes.Length);
        Assert.All(changes, c => Assert.Equal("State", code.Substring(c.Span.Start, c.Span.Length)));
    }
    [Fact]
    public void Generated_references_require_a_project_host_and_source_interface_contracts_are_renamed_together()
    {
        const string code = "partial class State { public int Value; }";
        Assert.Throws<InvalidOperationException>(() => Service(("Code.cs", code), ("State.g.cs", "partial class State { int Read() => Value; }"))
            .Rename("Code.cs", code.IndexOf("Value", StringComparison.Ordinal), "Number"));
        const string contract = "interface IState { int Read(); } class State : IState { public int Read() => 1; }";
        var plan = Service(("Code.cs", contract)).Rename("Code.cs", contract.LastIndexOf("Read", StringComparison.Ordinal), "Get");
        var changes = Assert.Single(plan.Documents).Changes;
        Assert.Equal(2, changes.Length);
        Assert.All(changes, change => Assert.Equal("Get", change.NewText));
    }
    [Fact]
    public void Keyword_names_are_escaped_and_invalid_names_are_rejected()
    {
        const string code = "class State { int Read() { var value = 1; return value; } }";
        var service = Service(("Code.cs", code)); var position = code.IndexOf("value", StringComparison.Ordinal);
        var plan = service.Rename("Code.cs", position, "class");
        Assert.All(Assert.Single(plan.Documents).Changes, c => Assert.Equal("@class", c.NewText));
        Assert.Throws<ArgumentException>(() => service.Rename("Code.cs", position, "a.b"));
        Assert.Throws<ArgumentException>(() => service.Rename("Code.cs", position, "@@class"));
    }
}
