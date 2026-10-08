using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpNavigationTests
{
    [Fact]
    public void Document_outline_preserves_nesting_partial_declarations_and_optional_locals()
    {
        const string code = """
            namespace Demo;
            public partial class Outer<T>
            {
                public int Field, Other;
                public int Value { get; set; }
                public int Run(int input) { int local = input; return local; }
                public class Inner { public int Deep; }
            }
            public record Entry(int Id);
            public enum Mode { One, Two }
            """;
        var project = new CSharpTestProject(("Code.cs", code), ("Outer.g.cs", "namespace Demo; public partial class Outer<T> { public int Generated; }"));
        project.AssertCompiles();
        var outline = project.Service.GetDocumentSymbols("Code.cs");
        Assert.False(outline.Truncated);
        var outer = Assert.Single(outline.Symbols, symbol => symbol.Name == "Outer");
        var ns = Assert.Single(outline.Symbols, symbol => symbol.Name == "Demo");
        Assert.Equal(ns.Id, outer.ParentId);
        Assert.Equal(2, outer.Locations.Length); Assert.Contains(outer.Locations, location => location.IsGenerated);
        Assert.Equal("Code.cs", outer.Extent!.Path);
        var inner = Assert.Single(outline.Symbols, symbol => symbol.Name == "Inner");
        Assert.Equal(outer.Id, inner.ParentId);
        Assert.Equal(inner.Id, Assert.Single(outline.Symbols, symbol => symbol.Name == "Deep").ParentId);
        Assert.DoesNotContain(outline.Symbols, symbol => symbol.Name is "input" or "local" or "T" or "Generated");
        Assert.DoesNotContain(outline.Symbols, symbol => symbol.Name is "get_Value" or "set_Value");
        var locals = project.Service.GetDocumentSymbols("Code.cs", includeLocals: true);
        var run = Assert.Single(locals.Symbols, symbol => symbol.Name == "Run");
        Assert.Equal(run.Id, Assert.Single(locals.Symbols, symbol => symbol.Name == "input").ParentId);
        Assert.Equal(run.Id, Assert.Single(locals.Symbols, symbol => symbol.Name == "local").ParentId);
        foreach (var symbol in locals.Symbols.Where(symbol => symbol.ParentId != null))
        {
            var parent = locals.Symbols[symbol.ParentId!.Value];
            Assert.True(parent.Extent!.Start <= symbol.Extent!.Start);
            Assert.True(parent.Extent.Start + parent.Extent.Length >= symbol.Extent.Start + symbol.Extent.Length);
        }
        var shallow = project.Service.GetDocumentSymbols("Code.cs", includeLocals: true, maximumDepth: 1);
        Assert.True(shallow.Truncated);
        Assert.DoesNotContain(shallow.Symbols, symbol => symbol.Name is "Inner" or "Deep" or "Run" or "local");
        var limited = project.Service.GetDocumentSymbols("Code.cs", maximumResults: 2);
        Assert.True(limited.Truncated); Assert.Equal(2, limited.Symbols.Length);
    }

    [Fact]
    public void Declaration_search_deduplicates_partial_types_and_excludes_generated_only_symbols_by_default()
    {
        var project = new CSharpTestProject(("One.cs", "namespace Demo; public partial class State { public int Count; }"),
            ("Two.cs", "namespace Demo; public partial class State { public int Other; }"),
            ("State.g.cs", "namespace Demo; public partial class State { public int GeneratedOnly; }"));
        project.AssertCompiles();
        var state = Assert.Single(project.Service.FindSymbols("Demo.State").Symbols, symbol => symbol.Name == "State");
        Assert.Equal(3, state.Locations.Length);
        Assert.Empty(project.Service.FindSymbols("generatedonly").Symbols);
        var generated = Assert.Single(project.Service.FindSymbols("generatedonly", includeGenerated: true).Symbols);
        Assert.True(Assert.Single(generated.Locations).IsGenerated);
        Assert.Single(project.Service.FindSymbols("count").Symbols);
        Assert.True(project.Service.FindSymbols(maximumResults: 1).Truncated);
    }

    [Fact]
    public void Generic_contract_navigation_finds_overrides_explicit_implementations_and_generated_members()
    {
        const string contract = "namespace Demo; public interface IValue<T> { T Value { get; } T Get(T value); }";
        const string source = """
            namespace Demo;
            public abstract class Base<T> : IValue<T> { public abstract T Value { get; } public virtual T Get(T value) => value; }
            public class IntValue : Base<int> { public override int Value => 42; public override int Get(int value) => value + 1; }
            public class Inherited : IntValue { }
            public class Explicit : IValue<int> { int IValue<int>.Value => 7; int IValue<int>.Get(int value) => value; }
            public partial class Generated : IValue<string> { }
            """;
        const string generated = "namespace Demo; partial class Generated { public string Value => \"ok\"; public string Get(string value) => value; }";
        var project = new CSharpTestProject(("IValue.cs", contract), ("Code.cs", source), ("Generated.g.cs", generated));
        project.AssertCompiles();
        var implementations = project.Service.GetImplementations("IValue.cs", contract.IndexOf("Get(", StringComparison.Ordinal));
        Assert.False(implementations.Truncated);
        Assert.Equal(new[] { source.IndexOf("Get(T", StringComparison.Ordinal), source.IndexOf("Get(int", StringComparison.Ordinal), source.LastIndexOf("Get(int", StringComparison.Ordinal) },
            implementations.Locations.Where(location => location.Path == "Code.cs").Select(location => location.Start));
        Assert.Equal(4, implementations.Locations.Length);
        Assert.Equal(3, implementations.Locations.Count(location => location.Path == "Code.cs"));
        Assert.True(Assert.Single(implementations.Locations, location => location.Path == "Generated.g.cs").IsGenerated);
        var properties = project.Service.GetImplementations("IValue.cs", contract.IndexOf("Value {", StringComparison.Ordinal));
        Assert.Equal(4, properties.Locations.Length);
        var overrides = project.Service.GetImplementations("Code.cs", source.IndexOf("Get(", StringComparison.Ordinal));
        var overridden = Assert.Single(overrides.Locations);
        Assert.Equal(source.IndexOf("Get(int", StringComparison.Ordinal), overridden.Start);
        var types = project.Service.GetImplementations("IValue.cs", contract.IndexOf("IValue", StringComparison.Ordinal));
        Assert.Equal(6, types.Locations.Length); // Five implementing types; Generated has two partial declarations.
        Assert.Single(types.Locations, location => location.IsGenerated);
        var limited = project.Service.GetImplementations("IValue.cs", contract.IndexOf("Get(", StringComparison.Ordinal), maximumResults: 2);
        Assert.True(limited.Truncated); Assert.Equal(2, limited.Locations.Length);
    }

    [Fact]
    public void Interface_dispatch_includes_overrides_and_reimplementation_but_excludes_hidden_members()
    {
        const string code = """
            interface IRun { int Run(); }
            class Base : IRun { public virtual int Run() => 1; }
            class Derived : Base { public override int Run() => 2; }
            class Hidden : Derived { public new int Run() => 3; }
            class Reimplemented : Base, IRun { public new int Run() => 4; }
            """;
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var implementations = project.Service.GetImplementations("Code.cs", code.IndexOf("Run();", StringComparison.Ordinal));
        var starts = implementations.Locations.Select(location => location.Start).ToArray();
        Assert.Equal(3, starts.Length);
        Assert.Contains(code.IndexOf("Run() => 1", StringComparison.Ordinal), starts);
        Assert.Contains(code.IndexOf("Run() => 2", StringComparison.Ordinal), starts);
        Assert.DoesNotContain(code.IndexOf("Run() => 3", StringComparison.Ordinal), starts);
        Assert.Contains(code.IndexOf("Run() => 4", StringComparison.Ordinal), starts);
    }

    [Fact]
    public void Type_navigation_uses_aliases_member_return_types_and_constructed_generic_definitions()
    {
        const string code = """
            using Alias = Demo.Box<int>;
            namespace Demo;
            public class Box<T> { public T Value = default!; }
            public class Probe { Alias box = new(); public Alias Create() => box; public object Run() => Create(); }
            """;
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        foreach (var offset in new[] { code.IndexOf("Alias box", StringComparison.Ordinal), code.IndexOf("box =", StringComparison.Ordinal), code.IndexOf("Create();", StringComparison.Ordinal) })
        {
            var definition = Assert.Single(project.Service.GetTypeDefinitions("Code.cs", offset));
            Assert.Equal(code.IndexOf("Box<T>", StringComparison.Ordinal), definition.Start);
        }
        var members = project.Service.GetMembers("Code.cs", code.IndexOf("box =", StringComparison.Ordinal), includeInherited: false);
        var value = Assert.Single(members.Symbols);
        Assert.Equal("Value", value.Name); Assert.Equal("int", value.Type);
    }

    [Fact]
    public void Member_and_type_hierarchy_inspection_includes_metadata_bases_and_respects_inheritance_options()
    {
        const string code = "namespace Demo; public interface IRoot { int Value { get; } } public interface IChild : IRoot { int Next(); } public class Base { protected int BaseField; } public class Child : Base, IChild { public int Value => 1; public int Next() => 2; } public class Leaf : Child {}";
        var project = new CSharpTestProject(("Code.cs", code)); project.AssertCompiles();
        var offset = code.IndexOf("Child : Base", StringComparison.Ordinal);
        var hierarchy = project.Service.GetTypeHierarchy("Code.cs", offset)!;
        Assert.False(hierarchy.Truncated); Assert.Equal("Child", hierarchy.Type.Name);
        Assert.Equal(["Base", "Object"], hierarchy.BaseTypes.Select(type => type.Name));
        Assert.Empty(hierarchy.BaseTypes[^1].Locations);
        Assert.Equal(["IChild", "IRoot"], hierarchy.Interfaces.Select(type => type.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Leaf", Assert.Single(hierarchy.DerivedTypes).Name);
        var own = project.Service.GetMembers("Code.cs", offset, includeInherited: false);
        Assert.Equal(["Next", "Value"], own.Symbols.Select(symbol => symbol.Name).Order(StringComparer.Ordinal));
        var inherited = project.Service.GetMembers("Code.cs", offset);
        Assert.Contains(inherited.Symbols, symbol => symbol.Name == "BaseField" && symbol.Accessibility == "Protected");
        Assert.Contains(inherited.Symbols, symbol => symbol.Name == "ToString" && symbol.Locations.IsEmpty);
        Assert.Contains(project.Service.GetMembers("Code.cs", offset, includeImplicit: true).Symbols, symbol => symbol.Kind == "Constructor");
        var interfaceMembers = project.Service.GetMembers("Code.cs", code.IndexOf("IChild :", StringComparison.Ordinal));
        Assert.Equal(["Next", "Value"], interfaceMembers.Symbols.Select(symbol => symbol.Name).Order(StringComparer.Ordinal));
        var limited = project.Service.GetTypeHierarchy("Code.cs", offset, maximumResults: 1)!;
        Assert.True(limited.Truncated); Assert.Empty(limited.BaseTypes); Assert.Empty(limited.Interfaces); Assert.Empty(limited.DerivedTypes);
    }

    [Fact]
    public void Navigation_validates_limits_and_obeys_cancellation()
    {
        var service = new CSharpTestProject(("Code.cs", "class Probe {}"), ("Empty.cs", "")).Service;
        Assert.Empty(service.GetDocumentSymbols("Empty.cs").Symbols);
        Assert.Empty(service.GetTypeDefinitions("Empty.cs", 0));
        Assert.Throws<ArgumentException>(() => service.FindSymbols(new string('x', 513)));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetDocumentSymbols("Code.cs", maximumDepth: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetMembers("Code.cs", 6, maximumResults: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetImplementations("Code.cs", -1));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.FindSymbols(cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetDocumentSymbols("Code.cs", cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetMembers("Code.cs", 6, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetImplementations("Code.cs", 6, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => service.GetTypeHierarchy("Code.cs", 6, cancellationToken: cancellation.Token));
    }
}
