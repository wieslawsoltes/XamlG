using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlG.Compiler;
using XamlG.Syntax;
using XamlG.Tooling;
using Xunit;

namespace XamlG.LanguageServer.Tests;

public sealed class ProjectAnalysisCacheTests
{
    private sealed class GatedPass : IXamlDocumentPass, IDisposable
    {
        public string Name => "Test barrier";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public BoundDocument Run(BoundDocument document, BindingContext context)
        {
            Started.TrySetResult(); Release.Wait(context.Cancellation); return document;
        }
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
    private static XamlCompilationSession Compiler(IXamlDocumentPass? pass = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Cache", new[] { CSharpSyntaxTree.ParseText("namespace Model { public class View {} }") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var profile = XamlFrameworkProfile.Portable;
        if (pass != null) profile = profile with { Passes = ImmutableArray.Create(pass) };
        return new(compilation, profile);
    }
    [Fact]
    public async Task ConcurrentWaitersShareOneComputationAndCancelIndependently()
    {
        using var pass = new GatedPass();
        var compiler = Compiler(pass); var store = new LspDocumentStore();
        store.Open("file:///View.xaml", 1, "<View xmlns='clr-namespace:Model'/>");
        var buffers = store.Capture();
        await using var cache = new LspProjectAnalysisCache();
        using var cancelled = new CancellationTokenSource();
        try
        {
            var first = cache.GetAsync(compiler, buffers, cancelled.Token);
            await pass.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var others = Enumerable.Range(0, 15).Select(_ => cache.GetAsync(compiler, buffers)).ToArray();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(1, cache.ComputationCount);
            Assert.All(others, task => Assert.False(task.IsCompleted));
            pass.Release.Set();
            var results = await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => Assert.Same(results[0][0], result[0]));
            Assert.True(results[0][0].Output.Success);
        }
        finally { pass.Release.Set(); }
    }
    [Fact]
    public async Task InvalidationCancelsSharedWorkAndNextRevisionComputesAgain()
    {
        using var pass = new GatedPass(); var compiler = Compiler(pass); var store = new LspDocumentStore();
        store.Open("file:///View.xaml", 1, "<View xmlns='clr-namespace:Model'/>");
        await using var cache = new LspProjectAnalysisCache();
        var old = cache.GetAsync(compiler, store.Capture());
        await pass.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cache.Invalidate();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        pass.Release.Set();
        store.Change("file:///View.xaml", 2, new[] { new LspTextChange(null, "<View xmlns='clr-namespace:Model' />") });
        var result = await cache.GetAsync(compiler, store.Capture()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(result).Output.Success); Assert.Equal(2, cache.ComputationCount);
    }
    [Fact]
    public async Task DifferentStoresWithEqualRevisionNumbersAreNotTheSameSnapshot()
    {
        var compiler = Compiler(); var first = new LspDocumentStore(); var second = new LspDocumentStore();
        first.Open("file:///View.xaml", 1, "<View xmlns='clr-namespace:Model'/>");
        second.Open("file:///View.xaml", 1, "<Missing xmlns='clr-namespace:Model'/>");
        await using var cache = new LspProjectAnalysisCache();
        Assert.True(Assert.Single(await cache.GetAsync(compiler, first.Capture())).Output.Success);
        Assert.False(Assert.Single(await cache.GetAsync(compiler, second.Capture())).Output.Success);
        Assert.Equal(2, cache.ComputationCount);
    }
}
