using System.Collections.Immutable;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ProjectDuplicateDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(1024)]
    public void Unique_declarations_preserve_original_records(int count)
    {
        var documents = Enumerable.Range(0, count).Select(i => Document("C" + i)).ToArray();
        var original = documents.ToArray();
        var addresses = Enumerable.Range(0, count).Select(i => (string?)("uri" + i)).ToArray();
        ProjectDuplicateDiagnostics.Apply(documents, addresses, default);
        for (var i = 0; i < count; i++) Assert.Same(original[i], documents[i]);
    }

    [Fact]
    public void Seeded_duplicates_match_frozen_grouped_diagnostics_and_record_identity()
    {
        var random = new Random(94073);
        string?[] names = [null, "", "A", "a", "B", "é", "e\u0301", "类", "urn:x", "urn:X"];
        for (var sample = 0; sample < 250; sample++)
        {
            var count = random.Next(0, 129);
            var documents = Enumerable.Range(0, count).Select(i => Document(names[random.Next(names.Length)]) with
            {
                IsSkipped = random.Next(4) == 0,
                Syntax = XamlSyntaxTree.Parse(i % 7 == 0 ? "<!--no root-->" : "<Node/>", "d" + i),
                Diagnostics = ImmutableArray.Create(new XamlDiagnostic("existing", "keep", new(i, 1),
                    random.Next(2) == 0 ? XamlSeverity.Warning : XamlSeverity.Error))
            }).ToArray();
            var addresses = Enumerable.Range(0, count).Select(_ => names[random.Next(names.Length)]).ToArray();
            var old = documents.ToArray();
            var current = documents.ToArray();
            Legacy(old, addresses);
            ProjectDuplicateDiagnostics.Apply(current, addresses, default);
            for (var i = 0; i < count; i++)
            {
                Assert.Equal(old[i].Diagnostics.ToArray(), current[i].Diagnostics.ToArray());
                Assert.Equal(ReferenceEquals(documents[i], old[i]), ReferenceEquals(documents[i], current[i]));
                Assert.Same(documents[i].Syntax, current[i].Syntax);
            }
        }
    }

    [Fact]
    public void Resource_diagnostic_precedes_class_diagnostic_and_skips_exclusions()
    {
        var documents = new[] { Document("C"), Document("C"), Document("C") with { IsSkipped = true } };
        ProjectDuplicateDiagnostics.Apply(documents, ["same", "same", "same"], default);
        Assert.Equal(new[] { "XG3300", "XG2002" }, documents[0].Diagnostics.Select(d => d.Code));
        Assert.Equal(documents[0].Diagnostics.ToArray(), documents[1].Diagnostics.ToArray());
        Assert.Empty(documents[2].Diagnostics);
    }

    [Fact]
    public void Sorted_path_scan_preserves_policy_calls_and_ignores_skipped_occurrences()
    {
        var inputs = new[] { "a", "a", "a", "b", "b", "c", "c", "d" }
            .Select((path, i) => new XamlProjectDocument(XamlSyntaxTree.Parse("<Node/>", "source" + i), path)).ToArray();
        var accepted = new[] { true, false, true, false, true, false, false, true };
        var calls = new List<string>();
        var policy = new Policy((syntax, _) => { var index = calls.Count; calls.Add(syntax.Path); return accepted[index]; });
        var actual = ProjectDuplicateDiagnostics.CollectSortedPaths(inputs, XamlFrameworkProfile.Portable with { Directives = policy }, new());
        Assert.Equal(new[] { "a" }, actual!);
        Assert.Equal(inputs.Select(input => input.Syntax.Path), calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unique_or_all_skipped_paths_need_no_duplicate_set(bool skipped)
    {
        var inputs = Enumerable.Range(0, 20).Select(i => new XamlProjectDocument(XamlSyntaxTree.Parse("<Node/>"), i.ToString("D2"))).ToArray();
        var calls = 0;
        var policy = new Policy((_, _) => { calls++; return !skipped; });
        Assert.Null(ProjectDuplicateDiagnostics.CollectSortedPaths(inputs, XamlFrameworkProfile.Portable with { Directives = policy }, new()));
        Assert.Equal(inputs.Length, calls);
    }

    [Fact]
    public void Stateful_policy_exception_is_not_suppressed_after_detecting_a_duplicate()
    {
        var inputs = Enumerable.Range(0, 4).Select(_ => new XamlProjectDocument(XamlSyntaxTree.Parse("<Node/>"), "a")).ToArray();
        var calls = 0;
        var error = new InvalidOperationException("policy");
        var policy = new Policy((_, _) => ++calls == 3 ? throw error : true);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
            ProjectDuplicateDiagnostics.CollectSortedPaths(inputs, XamlFrameworkProfile.Portable with { Directives = policy }, new())));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Pre_cancelled_diagnostic_pass_keeps_input_records()
    {
        var documents = new[] { Document("C"), Document("C") };
        var original = documents.ToArray();
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectDuplicateDiagnostics.Apply(documents, ["same", "same"], source.Token));
        Assert.Same(original[0], documents[0]);
        Assert.Same(original[1], documents[1]);
    }

    private static BoundDocument Document(string? name) => new(XamlSyntaxTree.Parse("<Node/>"), null, name, null,
        "public", [], [], XamlFrameworkProfile.Portable, new());

    // Frozen from b686b73: intentionally independent GroupBy implementation.
    private static void Legacy(BoundDocument[] bound, string?[] addresses)
    {
        foreach (var group in addresses.Select((uri, index) => (Uri: uri, Index: index))
            .Where(p => p.Uri != null && !bound[p.Index].IsSkipped).GroupBy(p => p.Uri, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = Error(bound[item.Index], "XG3300", "Duplicate resource URI: " + item.Uri);
        foreach (var group in bound.Select((d, i) => (Document: d, Index: i))
            .Where(p => !p.Document.IsSkipped && p.Document.ClassName != null).GroupBy(p => p.Document.ClassName, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var item in group) bound[item.Index] = Error(item.Document, "XG2002", "More than one XAML document declares x:Class '" + group.Key + "'.");
    }
    private static BoundDocument Error(BoundDocument document, string code, string message) => document with
    { Diagnostics = document.Diagnostics.Add(new XamlDiagnostic(code, message, document.Syntax.Root?.NameSpan ?? new TextSpan(0, 0))) };

    private sealed class Policy(Func<XamlSyntaxTree, XamlCompilerOptions, bool> select) : IXamlDirectivePolicy
    {
        private static IXamlDirectivePolicy Default => XamlFrameworkProfile.Portable.Directives;
        public bool ShouldCompile(XamlSyntaxTree syntax, XamlCompilerOptions options) => select(syntax, options);
        public IEnumerable<XamlDiagnostic> GetSkippedDiagnostics(XamlSyntaxTree syntax, CancellationToken cancellationToken) => Default.GetSkippedDiagnostics(syntax, cancellationToken);
        public string BindClassModifier(BindingContext context, XamlAttributeSyntax? directive) => Default.BindClassModifier(context, directive);
        public string BindFieldModifier(BindingContext context, XamlAttributeSyntax? directive) => Default.BindFieldModifier(context, directive);
    }
}
