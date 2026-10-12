using XamlG.Compiler;
using XamlG.CSharp;
using XamlG.Syntax;
using Xunit;

namespace XamlG.Tests;

public sealed class MappingStateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Explicit_state_preserves_nested_mappings_suppression_and_failure(bool lines, bool suppressed, bool fail)
    {
        var fixture = new EmissionAnalysisFixture();
        var document = fixture.Document with { Syntax = XamlSyntaxTree.Parse("<Node\n xmlns='clr-namespace:Traversal'/>", "map.xaml"),
            Options = fixture.Document.Options with { EmitLineDirectives = lines } };
        using var before = new EmissionContext(document, default);
        using var after = new EmissionContext(document, default);
        if (suppressed) { before.ConstructionParameters = new([], []); after.ConstructionParameters = new([], []); }
        var first = Run(before, true, fail);
        var second = Run(after, false, fail);
        Assert.Equal(first?.GetType(), second?.GetType());
        Assert.Equal(before.Writer.ToString(), after.Writer.ToString());
        Assert.Equal(before.Mappings.ToArray(), after.Mappings.ToArray());
        Assert.Equal(before.Diagnostics, after.Diagnostics);
    }

    [Fact]
    public void Suppressed_static_callback_has_no_per_call_capture_or_boxing()
    {
        var fixture = new EmissionAnalysisFixture();
        using var context = new EmissionContext(fixture.Document, default) { ConstructionParameters = new([], []) };
        for (var i = 0; i < 512; i++) MapNoOutput(context);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) MapNoOutput(context);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, bytes);
        Assert.Equal("unchanged", context.RootVariable);
        Assert.Empty(context.Mappings);
    }

    private static void MapNoOutput(EmissionContext context) => context.Map(default,
        (Context: context, Name: "unchanged"), static state => state.Context.RootVariable = state.Name);

    [Fact]
    public void Cancellation_precedes_state_callback_and_line_directive()
    {
        var fixture = new EmissionAnalysisFixture();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var context = new EmissionContext(fixture.Document, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => MapNoOutput(context));
        Assert.Equal("__root", context.RootVariable);
        Assert.Equal(string.Empty, context.Writer.ToString());
    }

    private static Exception? Run(EmissionContext context, bool previous, bool fail)
    {
        try
        {
            var span = new TextSpan(2, 5);
            if (previous) Previous(context, span, () => Body(context, previous, fail));
            else context.Map(span, (Context: context, Fail: fail), static state => Body(state.Context, false, state.Fail));
            return null;
        }
        catch (InvalidOperationException error) { return error; }
    }

    private static void Body(EmissionContext context, bool previous, bool fail)
    {
        context.Writer.Line("first");
        // Flip suppression during the outer callback. The outer mapping depends
        // on its entry state; the nested operation uses the new state.
        context.ConstructionParameters = context.ConstructionParameters == null ? new([], []) : null;
        var inner = new TextSpan(100, 2); // #line start clamps to the document length.
        if (previous) Previous(context, inner, () => context.Writer.Line("inner"));
        else context.Map(inner, context, static state => state.Writer.Line("inner"));
        context.Writer.Line("last");
        if (fail) throw new InvalidOperationException("mapped body");
    }

    // Frozen pre-change implementation; does not call the candidate's mapping helpers.
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
        var start = context.Writer.Position; emit();
        context.Mappings.Add(new(new(start, context.Writer.Position - start), span, document.Syntax.Path));
        if (document.Options.EmitLineDirectives && document.Syntax.Path.Length != 0) context.Writer.Line("#line default");
    }
}
