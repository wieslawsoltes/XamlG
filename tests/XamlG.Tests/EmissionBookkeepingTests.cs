using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class EmissionBookkeepingTests
{
    [Fact]
    public void Local_names_are_lazy_stable_and_do_not_freeze_shared_service_selection()
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        var local = context.ServicesType;
        Assert.Equal("__XamlGServices_" + context.Id, local);
        Assert.Same(local, context.ServicesType);
        Assert.Equal("__source_" + context.Id, context.SourceInfoTable);
        Assert.Same(context.SourceInfoTable, context.SourceInfoTable);
        Assert.Equal("__SetSourceInfo_" + context.Id, context.SourceInfoSetter());
        Assert.Same(context.SourceInfoSetter(), context.SourceInfoSetter());
        context.SharedServices = new("global::First.Services", "", ImmutableDictionary<string, string>.Empty);
        Assert.Equal("global::First.Services", context.ServicesType);
        context.SharedServices = new("global::Second.Services", "", ImmutableDictionary<string, string>.Empty);
        Assert.Equal("global::Second.Services", context.ServicesType);
        context.SharedServices = null;
        Assert.Same(local, context.ServicesType);
    }

    [Fact]
    public void Repeated_name_reads_allocate_no_strings_after_first_use()
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default);
        for (var i = 0; i < 512; i++) ReadNames(context);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) ReadNames(context);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private static void ReadNames(EmissionContext context)
    {
        GC.KeepAlive(context.ServicesType);
        GC.KeepAlive(context.SourceInfoTable);
        GC.KeepAlive(context.SourceInfoSetter());
        GC.KeepAlive(context.DocumentPathLiteral);
    }

    [Theory]
    [InlineData("")]
    [InlineData("view.xaml")]
    [InlineData("C:\\views\\quoted\"view.xaml")]
    [InlineData("line\r\n\t\0\u2028\u2029\ud800\udfff.xaml")]
    public void Path_literal_is_exact_and_owned_by_the_current_document(string path)
    {
        var fixture = new EmissionAnalysisFixture();
        var document = fixture.Document with { Syntax = XamlSyntaxTree.Parse("<Node/>", path) };
        using var context = new EmissionContext(document, default);
        Assert.Equal(CSharpNames.Literal(path), context.DocumentPathLiteral);
        Assert.Same(context.DocumentPathLiteral, context.DocumentPathLiteral);
        using var replacement = new EmissionContext(document with { Syntax = XamlSyntaxTree.Parse("<Node/>", path + "changed") }, default);
        Assert.Equal(CSharpNames.Literal(path + "changed"), replacement.DocumentPathLiteral);
        Assert.NotEqual(context.DocumentPathLiteral, replacement.DocumentPathLiteral);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Direct_mapping_preserves_entry_suppression_nesting_failure_and_offsets(bool lines, bool suppressed, bool fail)
    {
        var fixture = new EmissionAnalysisFixture();
        foreach (var path in new[] { "", "map.xaml", "C:\\quoted\"\r\n\ud800.xaml" })
        {
            var document = fixture.Document with
            {
                Syntax = XamlSyntaxTree.Parse("<Node\n xmlns='clr-namespace:Traversal'/>\r\n", path),
                Options = fixture.Document.Options with { EmitLineDirectives = lines }
            };
            using var previous = new EmissionContext(document, default);
            using var current = new EmissionContext(document, default);
            previous.Writer.Indent = current.Writer.Indent = 7;
            if (suppressed) { previous.ConstructionParameters = new([], []); current.ConstructionParameters = new([], []); }
            var oldFailure = Run(previous, false, fail);
            var newFailure = Run(current, true, fail);
            Assert.Equal(oldFailure?.GetType(), newFailure?.GetType());
            Assert.Equal(previous.Writer.ToString(), current.Writer.ToString());
            Assert.Equal(previous.Writer.Position, current.Writer.Position);
            Assert.Equal(previous.Mappings.ToArray(), current.Mappings.ToArray());
            Assert.Equal(previous.Diagnostics, current.Diagnostics);
        }
    }

    [Fact]
    public void Cancellation_precedes_any_direct_mapping_output()
    {
        var fixture = new EmissionAnalysisFixture();
        using var cancellation = new CancellationTokenSource();
        using var context = new EmissionContext(fixture.Document, cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => context.BeginMap(default));
        Assert.Empty(context.Mappings);
        Assert.Equal(string.Empty, context.Writer.ToString());
    }

    [Fact]
    public void Fragmented_directives_preserve_spelling_indentation_and_positions()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("ar-SA"), CultureInfo.GetCultureInfo("pl-PL") })
            {
                CultureInfo.CurrentCulture = culture;
                var old = new CSharpWriter { Indent = 9 };
                var current = new CSharpWriter { Indent = 9 };
                foreach (var number in new[] { 1, 17, 1024, int.MaxValue })
                {
                    const string path = "C:\\path\\file\"name.xaml";
                    old.Line("#line " + number + " " + CSharpNames.Literal(path));
                    current.LineDirective(number.ToString(), CSharpNames.Literal(path));
                    old.Line("value;"); current.Line("value;");
                    Assert.Equal(old.Position, current.Position);
                }
                Assert.Equal(old.ToString(), current.ToString());
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void Quoted_directive_filename_preserves_previous_compiler_diagnostics()
    {
        // #line filenames do not process C# string escapes. This performance
        // pass preserves that pre-existing limitation instead of calling such
        // a path a successfully compilable benchmark fixture.
        var fixture = new EmissionAnalysisFixture();
        var document = fixture.Document with
        {
            Syntax = XamlSyntaxTree.Parse("<Node/>", "quoted\"name.xaml"),
            Options = fixture.Document.Options with { EmitLineDirectives = true }
        };
        using var old = new EmissionContext(document, default);
        using var current = new EmissionContext(document, default);
        Previous(old, default, () => old.Writer.Line("class C { }"));
        var start = current.BeginMap(default);
        current.Writer.Line("class C { }");
        current.EndMap(default, start);
        var before = CSharpSyntaxTree.ParseText(old.Writer.ToString()).GetDiagnostics()
            .Select(d => (d.Id, d.GetMessage(), d.Location.SourceSpan)).ToArray();
        var after = CSharpSyntaxTree.ParseText(current.Writer.ToString()).GetDiagnostics()
            .Select(d => (d.Id, d.GetMessage(), d.Location.SourceSpan)).ToArray();
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
        Assert.Equal(old.Writer.ToString(), current.Writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complete_emissions_are_independent_deterministic_and_compile(bool lines)
    {
        const string model = """
            using System.Collections.Generic;
            using XamlG.Runtime;
            namespace Bookkeeping {
                public class Root { [Content] public List<Item> Children { get; } = new(); }
                public class Item { public string Text { get; set; } = ""; public int Number { get; set; } }
            }
            """;
        var compilation = CompilationFactory.Create(model);
        var xaml = "<Root xmlns='clr-namespace:Bookkeeping'>\n" +
            string.Concat(Enumerable.Range(0, 32).Select(i => "<Item Text='value' Number='" + i + "'/>\n")) + "</Root>";
        var outputs = new string[8];
        Parallel.For(0, outputs.Length, i =>
        {
            var document = new XamlCompiler().Bind(XamlSyntaxTree.Parse(xaml, "views\\deep\\name.xaml"), compilation,
                XamlFrameworkProfile.Portable, new XamlCompilerOptions { EmitLineDirectives = lines });
            Assert.True(document.Success, string.Join("\n", document.Diagnostics));
            var result = new CSharpEmitter().Emit(document);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            outputs[i] = result.Source;
            using var stream = new MemoryStream();
            var compiled = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(result.Source,
                (CSharpParseOptions)compilation.SyntaxTrees.First().Options)).Emit(stream);
            Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        });
        foreach (var output in outputs) Assert.Equal(outputs[0], output);
    }

    private static Exception? Run(EmissionContext context, bool direct, bool fail)
    {
        try
        {
            var outer = new TextSpan(2, 5);
            if (direct)
            {
                var start = context.BeginMap(outer);
                Body(context, true, fail);
                context.EndMap(outer, start);
            }
            else Previous(context, outer, () => Body(context, false, fail));
            return null;
        }
        catch (InvalidOperationException error) { return error; }
    }

    private static void Body(EmissionContext context, bool direct, bool fail)
    {
        context.Writer.Line("first");
        context.ConstructionParameters = context.ConstructionParameters == null ? new([], []) : null;
        var inner = new TextSpan(1000, 2);
        if (direct)
        {
            var start = context.BeginMap(inner);
            context.Writer.Line("nested");
            context.EndMap(inner, start);
        }
        else Previous(context, inner, () => context.Writer.Line("nested"));
        context.Writer.Line("last");
        if (fail) throw new InvalidOperationException("mapped failure");
    }

    // Frozen original mapping; never calls BeginMap/EndMap or cached spellings.
    private static void Previous(EmissionContext context, TextSpan span, Action emit)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (context.ConstructionParameters != null) { emit(); return; }
        var document = context.Document;
        if (document.Options.EmitLineDirectives && document.Syntax.Path.Length != 0)
        {
            var offset = Math.Min(span.Start, document.Syntax.Text.Length);
            context.Writer.Line("#line " + (document.Syntax.Lines.GetPosition(offset).Line + 1) + " " + CSharpNames.Literal(document.Syntax.Path));
        }
        var start = context.Writer.Position;
        emit();
        context.Mappings.Add(new(new(start, context.Writer.Position - start), span, document.Syntax.Path));
        if (document.Options.EmitLineDirectives && document.Syntax.Path.Length != 0) context.Writer.Line("#line default");
    }
}
