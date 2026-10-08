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

    [Fact]
    public void Generic_interface_override_and_explicit_implementation_contracts_follow_one_rename()
    {
        const string contracts = "public interface IState<T> { T Read(T value); } public abstract class Base<T> : IState<T> { public abstract T Read(T value); }";
        const string implementations = "public class State : Base<int> { public override int Read(int value) => value; } public class Explicit : IState<int> { int IState<int>.Read(int value) => value; }";
        const string uses = "class Use { int Get(IState<int> contract, State concrete) => contract.Read(1) + concrete.Read(2); }";
        var plan = Service(("Contracts.cs", contracts), ("State.cs", implementations), ("Use.cs", uses))
            .Rename("State.cs", implementations.IndexOf("Read", StringComparison.Ordinal), "Evaluate");
        Assert.Equal(3, plan.Documents.Length);
        Assert.Equal(6, plan.Documents.Sum(document => document.Changes.Length));
        Assert.All(plan.Documents.SelectMany(document => document.Changes), change => Assert.Equal("Evaluate", change.NewText));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Record_parameter_and_property_rename_updates_construction_with_and_deconstruction(bool parameter)
    {
        const string record = "public record State(int Value, string Label);";
        const string use = "class Use { int Read(State state) { var (value, label) = state; return state.Value + (state with { Value = 2 }).Value + new State(Value: 3, Label: label).Value; } }";
        var plan = Service(("State.cs", record), ("Use.cs", use)).Rename(parameter ? "State.cs" : "Use.cs",
            parameter ? record.IndexOf("Value", StringComparison.Ordinal) : use.IndexOf("Value", StringComparison.Ordinal), "Number");
        Assert.Equal(2, plan.Documents.Length);
        Assert.Single(plan.Documents.Single(document => document.Path == "State.cs").Changes);
        Assert.Equal(5, plan.Documents.Single(document => document.Path == "Use.cs").Changes.Length);
        Assert.All(plan.Documents.SelectMany(document => document.Changes), change => Assert.Equal("Number", change.NewText));
    }

    [Fact]
    public void Alias_rename_does_not_change_the_aliased_type()
    {
        const string code = "using Alias = Model.State; namespace Model { public class State { } } class Use { Alias Read() => new Alias(); }";
        var plan = Service(("Code.cs", code)).Rename("Code.cs", code.IndexOf("Alias", StringComparison.Ordinal), "ViewModel");
        var edits = Assert.Single(plan.Documents).Changes;
        Assert.Equal(3, edits.Length);
        Assert.All(edits, change => Assert.Equal("Alias", code.Substring(change.Span.Start, change.Span.Length)));
    }

    [Fact]
    public void Metadata_contract_rename_is_rejected_instead_of_breaking_implementation()
    {
        const string code = "public class Resource : System.IDisposable { public void Dispose() {} }";
        Assert.Throws<InvalidOperationException>(() => Service(("Code.cs", code)).Rename("Code.cs", code.IndexOf("Dispose()", StringComparison.Ordinal), "Close"));
    }

    [Fact]
    public void Type_parameter_rename_must_not_capture_an_outer_parameter_with_the_same_ordinal()
    {
        const string code = "class Outer<U> { public class Inner<T> { public U Read() => default; } }";
        Assert.Throws<InvalidOperationException>(() => Service(("Code.cs", code)).Rename("Code.cs", code.IndexOf("<T>", StringComparison.Ordinal) + 1, "U"));
    }
}
