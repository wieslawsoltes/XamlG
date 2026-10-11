using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using XamlG.Compiler;
using XamlG.Roslyn;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class ContentNormalizationTests
{
    internal const string Model = """
        using System.Collections.Generic;
        using XamlG.Runtime;
        namespace WhitespaceFixture {
            [WhitespaceSignificantCollection] public class Words : List<object> { }
            public class Plain { }
            [TrimSurroundingWhitespace] public class Trim { }
            public class Root { [Content] public Words Children { get; } = new(); }
            public class TextRoot { [Content] public string Text { get; set; } = ""; }
        }
        """;
    private const string Header = "<Root xmlns='clr-namespace:WhitespaceFixture' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    private static readonly Type Implementation = typeof(XamlCompiler).Assembly.GetType("XamlG.Compiler.ContentWhitespaceNormalizer", throwOnError: true)!;
    private delegate XamlSyntaxNode[] NormalizeCall(IEnumerable<XamlSyntaxNode> source, ITypeSymbol target, bool collection, NamespaceScope scope, bool direct);
    private static NormalizeCall Create(BindingContext context) => (NormalizeCall)Implementation.GetMethod("Normalize")!
        .CreateDelegate(typeof(NormalizeCall), Activator.CreateInstance(Implementation, context)!);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Literal_whitespace_neighbor_and_callback_semantics_match_frozen_normalizer(bool collection, bool direct)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var bodies = new[] {
            "", " \t\r\n", "a<!--ignored-->b<![CDATA[ c]]>d", "<![CDATA[ first ]]><!--c--> second ",
            " <Plain/> \t <Trim/> tail ", "<Trim/><x:String> </x:String> a <Plain/>",
            "<x:String xml:space='preserve'>  a   </x:String><x:String> b   c </x:String>",
            "<x:String xml:space='default'>  a   </x:String>  b  ",
            "<Plain> <Trim/> </Plain> \u00a0 <Trim/>", "<x:String xml:lang='en' xml:base='.'> a b </x:String>",
            "<x:String Unknown='reject'> a </x:String> <x:String/>", "<Switch/> a <Switch/> b <Switch/>",
            "\u00a0 <Trim/> \u2003", "<Missing/> \t <Trim/> tail", "<?probe test?>text<!--tail-->"
        };
        foreach (var preserve in new[] { false, true })
            foreach (var target in new[] { types.Find("WhitespaceFixture.Words")!, types.Find("WhitespaceFixture.Plain")!, types.Special(SpecialType.System_String) })
                foreach (var body in bodies)
                    Equivalent(types, Header + (preserve ? " xml:space='preserve'" : "") + ">" + body + "</Root>", target, collection, direct);
    }

    [Fact]
    public void Seeded_content_sequences_preserve_original_neighbors_under_compaction()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var random = new Random(15491);
        var terms = new[] { " ", "\n\t", " a ", "b", "<!--c-->", "<![CDATA[ c ]]>", "<Plain/>", "<Trim/>",
            "<Switch/>", "<x:String> </x:String>", "<x:String> a </x:String>", "<x:String xml:space='preserve'>  </x:String>", "\u00a0" };
        for (var sample = 0; sample < 200; sample++)
        {
            var source = Header + (sample % 2 == 0 ? " xml:space='preserve'" : "") + ">" +
                string.Concat(Enumerable.Range(0, random.Next(1, 32)).Select(_ => terms[random.Next(terms.Length)])) + "</Root>";
            Equivalent(types, source, types.Find("WhitespaceFixture.Words")!, true, sample % 3 == 0);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(1024)]
    public void Fragment_runs_keep_first_cdata_kind_complete_span_and_one_pass_enumeration(int count)
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var source = Header + " xml:space='preserve'><![CDATA[first]]>" +
            string.Concat(Enumerable.Repeat("<!--gap-->x<![CDATA[y]]>", count)) + "</Root>";
        var tree = XamlSyntaxTree.Parse(source);
        var scope = NamespaceScope.Empty.Push(tree.Root!);
        var nodes = new Once(tree.Root!.Children);
        var result = Create(Context(tree, types, new Observe()))(nodes, types.Special(SpecialType.System_String), false, scope, false);
        var text = Assert.IsType<XamlTextSyntax>(Assert.Single(result));
        Assert.True(text.IsCData);
        Assert.Equal("first" + string.Concat(Enumerable.Repeat("xy", count)), text.Value);
        Assert.Equal(TextSpan.FromBounds(tree.Root.Children.First(n => n is XamlTextSyntax).Span.Start,
            tree.Root.Children.Last(n => n is XamlTextSyntax).Span.End), text.Span);
        Assert.Equal(1, nodes.Enumerations); Assert.True(nodes.Disposed);
    }

    [Fact]
    public void Wide_literal_xml_attributes_preserve_namespace_shadowing_and_text()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var attributes = string.Join(" ", Enumerable.Range(0, 512).Select(i => "xml:p" + i + "='v'"));
        Equivalent(types, Header + "><x:String xmlns:a='urn:outer' " + attributes + " xml:space='default'> a  b </x:String></Root>",
            types.Find("WhitespaceFixture.Words")!, true, false);
    }

    [Fact]
    public void Cancellation_during_fragment_enumeration_disposes_the_enumerator_without_mutating_input()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        var tree = XamlSyntaxTree.Parse(Header + ">a<!--c-->b</Root>");
        var before = Snapshot(tree.Root!);
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        IEnumerable<XamlSyntaxNode> Source()
        {
            try
            {
                yield return tree.Root!.Children[0];
                cancellation.Cancel();
                yield return tree.Root.Children[2];
            }
            finally { disposed = true; }
        }
        var normalize = Create(Context(tree, types, new Observe(), cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => normalize(Source(), types.Special(SpecialType.System_String), false, NamespaceScope.Empty, false));
        Assert.True(disposed); Assert.Equal(before, Snapshot(tree.Root!));
        Assert.Throws<OperationCanceledException>(() => normalize(Array.Empty<XamlSyntaxNode>(), types.Special(SpecialType.System_String), false, NamespaceScope.Empty, false));
        var recovered = Create(Context(tree, types, new Observe()))(tree.Root!.Children, types.Special(SpecialType.System_String), false, NamespaceScope.Empty, false);
        Assert.Equal("ab", Assert.IsType<XamlTextSyntax>(Assert.Single(recovered)).Value);
    }

    [Fact]
    public void Separate_normalizers_can_share_immutable_input_concurrently()
    {
        var types = new RoslynTypeSystem(CompilationFactory.Create(Model));
        Parallel.For(0, 16, _ => Equivalent(types, Header + "><Trim/> a<!--c--><![CDATA[ b ]]><x:String> c </x:String><Plain/></Root>",
            types.Find("WhitespaceFixture.Words")!, true, false));
    }

    private static BindingContext Context(XamlSyntaxTree tree, RoslynTypeSystem types, Observe rule, CancellationToken token = default) =>
        BindingContext.CreateSignatureProbe(tree, types, XamlFrameworkProfile.Portable with { TypeBindingRules = ImmutableArray.Create<IXamlTypeBindingRule>(rule) }, new(), token);

    private static void Equivalent(RoslynTypeSystem types, string source, ITypeSymbol target, bool collection, bool direct)
    {
        var tree = XamlSyntaxTree.Parse(source, "Content.xaml");
        var root = tree.Root!; var scope = NamespaceScope.Empty.Push(root); var snapshot = Snapshot(root);
        var expectedRule = new Observe(); var actualRule = new Observe();
        var expectedContext = Context(tree, types, expectedRule); var actualContext = Context(tree, types, actualRule);
        var expected = new FrozenContentWhitespaceNormalizer(expectedContext).Normalize(root.Children, target, collection, scope, direct);
        var sequence = new Once(root.Children);
        var actual = Create(actualContext)(sequence, target, collection, scope, direct);
        Assert.Equal(expected.Select(Snapshot), actual.Select(Snapshot));
        Assert.Equal(expectedContext.Diagnostics, actualContext.Diagnostics);
        Assert.Equal(expectedRule.Trace, actualRule.Trace);
        Assert.Equal(expectedContext.Symbols.Select(s => (s.Span, s.Role, s.Symbol.ToDisplayString())), actualContext.Symbols.Select(s => (s.Span, s.Role, s.Symbol.ToDisplayString())));
        Assert.Equal(snapshot, Snapshot(root)); Assert.Equal(1, sequence.Enumerations); Assert.True(sequence.Disposed);
    }

    private static string Snapshot(XamlSyntaxNode node) => JsonSerializer.Serialize(node, node.GetType()) +
        (node is XamlElementSyntax element ? string.Concat(element.Children.Select(Snapshot)) : "");

    private sealed class Observe : IXamlTypeBindingRule
    {
        public List<string> Trace { get; } = new();
        public bool TryResolve(BindingContext context, XamlTypeNameSyntax syntax, NamespaceScope scope, out INamedTypeSymbol? type)
        {
            Trace.Add(syntax.Name + ":" + syntax.Span + ":" + scope.PreserveSpace);
            // Stateful resolution makes it invalid to hoist or memoize trim probes.
            type = syntax.Name == "Switch" ? context.Types.Find(Trace.Count % 2 == 0 ? "WhitespaceFixture.Trim" : "WhitespaceFixture.Plain") : null;
            return type != null;
        }
    }

    private sealed class Once(IEnumerable<XamlSyntaxNode> nodes) : IEnumerable<XamlSyntaxNode>
    {
        public int Enumerations { get; private set; }
        public bool Disposed { get; private set; }
        public IEnumerator<XamlSyntaxNode> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("Input was enumerated twice.");
            try { foreach (var node in nodes) yield return node; }
            finally { Disposed = true; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
