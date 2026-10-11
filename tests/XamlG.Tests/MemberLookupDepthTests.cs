using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp.Resources;
using XamlG.Roslyn;
using XamlG.Runtime;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MemberLookupDepthTests
{
    private static readonly XamlSyntaxTree Syntax = XamlSyntaxTree.Parse("<Root/>");

    // The pre-optimization selector remains the independent correctness oracle.
    private static ISymbol? Legacy(RoslynTypeSystem types, ITypeSymbol target, string name, INamedTypeSymbol? within) =>
        (ISymbol?)target.Members(name).OfType<IPropertySymbol>().FirstOrDefault(p => !p.IsStatic && !p.IsIndexer && types.IsAccessible(p, within)) ??
        target.Members(name).OfType<IEventSymbol>().FirstOrDefault(e => !e.IsStatic && types.IsAccessible(e, within));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    public void Class_depth_boundary_preserves_property_priority_and_context_access(int depth)
    {
        var model = """
            using System;
            public class Root {
                public string Shared { get; set; } = "";
                protected string Guard { get; set; } = "";
                private string Hidden { get; set; } = "";
                public event Action Changed { add {} remove {} }
            }
            public class Other { }
            """ + string.Concat(Enumerable.Range(1, depth).Select(i =>
                "public class D" + i + " : " + (i == 1 ? "Root" : "D" + (i - 1)) + " { " +
                (i == 1 ? "public new event Action Shared { add {} remove {} }" : "") + " }"));
        var compilation = CompilationFactory.Create(model);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var target = compilation.GetTypeByMetadataName(depth == 0 ? "Root" : "D" + depth)!;
        // New type systems share the Roslyn type, not the context-dependent selection.
        for (var round = 0; round < 3; round++)
        {
            var types = new RoslynTypeSystem(compilation);
            var context = BindingContext.CreateSignatureProbe(Syntax, types, XamlFrameworkProfile.Portable, new());
            foreach (var within in new INamedTypeSymbol?[] { null, target, types.Find("Root"), types.Find("Other"), null })
            {
                context.RootClass = within;
                foreach (var name in new[] { "Shared", "Guard", "Hidden", "Changed", "Missing", "shared" })
                {
                    var expected = Legacy(types, target, name, within);
                    var span = new TextSpan(round, 1);
                    var actual = context.Members.Resolve(target, name, NamespaceScope.Empty, span, report: false);
                    Assert.True(SymbolEqualityComparer.IncludeNullability.Equals(expected, actual?.Symbol), name);
                    if (actual != null) Assert.Equal(span, actual.Span);
                }
            }
            Assert.Empty(context.Diagnostics);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(32)]
    public void Interface_boundary_keeps_all_properties_before_any_event(int depth)
    {
        var model = "using System; public interface I0 { string Value { get; set; } event Action Changed; }" +
            string.Concat(Enumerable.Range(1, depth).Select(i => "public interface I" + i + " : I" + (i - 1) +
                " { " + (i == 1 ? "new event Action Value;" : "") + " }"));
        var compilation = CompilationFactory.Create(model);
        Assert.DoesNotContain(compilation.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
        var target = compilation.GetTypeByMetadataName("I" + depth)!;
        for (var round = 0; round < 3; round++)
        {
            var types = new RoslynTypeSystem(compilation);
            var context = BindingContext.CreateSignatureProbe(Syntax, types, XamlFrameworkProfile.Portable, new());
            foreach (var name in new[] { "Value", "Changed", "Missing" })
            {
                var expected = Legacy(types, target, name, null);
                var actual = context.Members.Resolve(target, name, NamespaceScope.Empty, default, report: false);
                Assert.True(SymbolEqualityComparer.Default.Equals(expected, actual?.Symbol));
                if (name == "Value") Assert.IsAssignableFrom<IPropertySymbol>(actual!.Symbol);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Member_conversion_and_publication_caches_survive_compilation_replacement(int concurrency)
    {
        const string model = """
            using System;
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace Combined {
                public class Parsed {
                    public static Parsed Parse(string text, IFormatProvider provider) => new();
                }
                public class Item { public Parsed Value { get; set; } = new(); }
                public class Panel { [Content] public List<Item> Children { get; } = new(); }
            }
            """;
        var originals = new[] { "First.xaml", "Second.xaml" }.Select(path => new XamlProjectDocument(
            XamlSyntaxTree.Parse("<Panel xmlns='clr-namespace:Combined'><Item Value='value'/></Panel>", path), path)).ToArray();
        var first = CompilationFactory.Create(model).AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var text = CompilationFactory.Create(model.Replace("public Parsed Value { get; set; } = new();", "public string Value { get; set; } = \"\";"))
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var inaccessible = CompilationFactory.Create(model.Replace("public Parsed Value", "private Parsed Value"))
            .AddReferences(MetadataReference.CreateFromFile(typeof(XamlRuntimeContext).Assembly.Location));
        var compiler = new XamlProjectCompiler();
        var options = new XamlCompilerOptions { MaxDegreeOfParallelism = concurrency };
        foreach (var compilation in new[] { first, first, text, inaccessible, text, first })
        {
            var actual = compiler.Compile(originals, compilation, options: options);
            var fresh = new XamlProjectCompiler().Compile(originals, compilation, options: options);
            Assert.Equal(!ReferenceEquals(compilation, inaccessible), actual.Success);
            Assert.Equal(fresh.Success, actual.Success);
            for (var i = 0; i < originals.Length; i++)
            {
                Assert.Equal(fresh.Documents[i].Document.Diagnostics.ToArray(), actual.Documents[i].Document.Diagnostics.ToArray());
                Assert.Equal(fresh.Documents[i].Output.Diagnostics.ToArray(), actual.Documents[i].Output.Diagnostics.ToArray());
                Assert.Equal(fresh.Documents[i].Output.Source, actual.Documents[i].Output.Source);
                Assert.Equal(fresh.Documents[i].Output.SourceMappings.ToArray(), actual.Documents[i].Output.SourceMappings.ToArray());
            }
            if (actual.Success)
            {
                var generated = actual.Documents.Select(d => CSharpSyntaxTree.ParseText(d.Output.Source, new CSharpParseOptions(LanguageVersion.Preview)));
                using var image = new MemoryStream();
                var result = compilation.AddSyntaxTrees(generated).Emit(image);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            }
        }
    }
}
