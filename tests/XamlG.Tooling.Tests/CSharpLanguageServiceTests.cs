using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace XamlG.Tooling.Tests;

public sealed class CSharpLanguageServiceTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private static CSharpLanguageService Service(params (string Path, string Text)[] files) => new(
        CSharpCompilation.Create("Authoring", files.Select(f => CSharpSyntaxTree.ParseText(f.Text, new CSharpParseOptions(LanguageVersion.Preview), f.Path)),
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)), files.Where(f => !f.Path.EndsWith(".g.cs", StringComparison.Ordinal)).Select(f => f.Path));

    [Fact]
    public void Navigation_uses_symbols_across_files_and_preserves_generated_source_locations()
    {
        const string declaration = "public partial class State { public const string Text = \"ok\"; }";
        const string use = "partial class State { string Get() => Text + nameof(Text) + \"Text\"; string Other() { var Text = \"local\"; return Text; } }";
        const string generated = "partial class State { string Generated() => Text; }";
        var service = Service(("State.cs", declaration), ("View.cs", use), ("State.g.cs", generated));
        var symbol = service.GetSymbol("View.cs", use.IndexOf("Text", StringComparison.Ordinal))!;
        Assert.Equal("Field", symbol.Kind); Assert.Equal("string", symbol.Type); Assert.Equal("ok", symbol.Constant);
        Assert.Equal("State.cs", Assert.Single(symbol.Locations).Path);
        var references = service.GetReferences("State.cs", declaration.IndexOf("Text", StringComparison.Ordinal));
        Assert.False(references.Truncated); Assert.Equal(4, references.Locations.Length);
        Assert.Equal(2, references.Locations.Count(l => l.Path == "View.cs"));
        Assert.True(Assert.Single(references.Locations.Where(l => l.Path == "State.g.cs")).IsGenerated);
        Assert.Null(service.GetSymbol("View.cs", use.IndexOf("\"Text\"", StringComparison.Ordinal) + 1)!.Kind);
        Assert.Throws<InvalidOperationException>(() => service.Format("State.g.cs"));
    }

    [Fact]
    public void Completion_obeys_receiver_accessibility_inheritance_and_replacement_span()
    {
        const string model = "public class Base { public string Text => \"\"; public static int Total; private int Trap; } public class State : Base { public int Thing; }";
        const string use = "class View { void Read(State value) { var result = value.Th; } }";
        var service = Service(("State.cs", model), ("View.cs", use));
        var offset = use.IndexOf(".Th", StringComparison.Ordinal) + 3;
        var result = service.GetCompletions("View.cs", offset);
        Assert.Equal("Th", use.Substring(result.Span.Start, result.Span.Length));
        Assert.Equal("Thing", Assert.Single(result.Items).Label);
        const string all = "class View { void Read(State value) { var result = value.; } }";
        var complete = Service(("State.cs", model), ("View.cs", all)).GetCompletions("View.cs", all.IndexOf("value.;", StringComparison.Ordinal) + 6);
        Assert.Contains(complete.Items, i => i.Label == "Text");
        Assert.DoesNotContain(complete.Items, i => i.Label is "Total" or "Trap");
    }

    [Fact]
    public void Signature_help_uses_overload_resolution_and_argument_position()
    {
        const string code = "class State { void Read(string value, int count) {} void Read(int value) {} void Use() { Read(\"ok\", 2); } }";
        var result = Service(("Code.cs", code)).GetSignatures("Code.cs", code.IndexOf(", 2", StringComparison.Ordinal) + 2);
        Assert.Equal(1, result.ActiveParameter); Assert.Equal(2, result.Signatures.Length);
        Assert.Equal(2, Assert.Single(result.Signatures.Where(s => s.IsSelected)).Parameters.Length);
    }

    [Fact]
    public void Source_actions_preserve_types_and_reject_target_typed_construction()
    {
        const string code = "class State { void Use() { var count = 42; State state = new(); object boxed = 42; } }";
        var service = Service(("Code.cs", code));
        var action = Assert.Single(service.GetActions("Code.cs", code.IndexOf("count", StringComparison.Ordinal)));
        Assert.Equal("Use explicit type", action.Title);
        var updated = Microsoft.CodeAnalysis.Text.SourceText.From(code).WithChanges(action.Changes.Select(c =>
            new Microsoft.CodeAnalysis.Text.TextChange(new(c.Span.Start, c.Span.Length), c.NewText))).ToString();
        Assert.Contains("int count = 42;", updated);
        Assert.Empty(service.GetActions("Code.cs", code.IndexOf("state =", StringComparison.Ordinal)));
        Assert.Empty(service.GetActions("Code.cs", code.IndexOf("boxed", StringComparison.Ordinal)));
    }

    [Fact]
    public void Formatting_keeps_literal_and_comment_contents()
    {
        const string code = "class State{ // keep this\nstring Value()=>\"two  spaces\";}";
        var changes = Service(("Code.cs", code)).Format("Code.cs");
        var updated = Microsoft.CodeAnalysis.Text.SourceText.From(code).WithChanges(changes.Select(c =>
            new Microsoft.CodeAnalysis.Text.TextChange(new(c.Span.Start, c.Span.Length), c.NewText))).ToString();
        Assert.Contains("\"two  spaces\"", updated); Assert.Contains("// keep this", updated);
        Assert.Empty(Service(("Code.cs", updated)).Format("Code.cs"));
    }

    [Fact]
    public void Use_var_never_loses_target_typing_or_changes_nullable_type()
    {
        const string code = "#nullable enable\nclass State { void Use(bool test) { int value = default; int? optional = test ? 1 : null; string? nullable = \"text\"; } }";
        var service = Service(("Code.cs", code));
        foreach (var name in new[] { "value", "optional", "nullable" })
            Assert.Empty(service.GetActions("Code.cs", code.IndexOf(name, StringComparison.Ordinal)));
    }

    [Fact]
    public void Incomplete_buffers_and_limits_are_explicit()
    {
        const string code = "class State { int Value; int Read() => Value + Value; }";
        var service = Service(("Code.cs", code));
        var references = service.GetReferences("Code.cs", code.IndexOf("Value", StringComparison.Ordinal), maximumResults: 1);
        Assert.True(references.Truncated); Assert.Single(references.Locations);
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetCompletions("Code.cs", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GetReferences("Code.cs", 0, maximumResults: 10001));
        Assert.Null(Service(("Empty.cs", "")).GetSymbol("Empty.cs", 0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.GetReferences("Code.cs", code.IndexOf("Value", StringComparison.Ordinal), cancellationToken: cancellation.Token));
    }
}
