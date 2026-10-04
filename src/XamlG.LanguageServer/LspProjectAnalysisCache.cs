using System.Collections.Immutable;
using XamlG.Tooling;

namespace XamlG.LanguageServer;

/// <summary>Shares a single semantic computation for each immutable project/buffer-set pair.
/// Cancelling one waiter never cancels other consumers; invalidation retires the shared work.</summary>
public sealed class LspProjectAnalysisCache : IAsyncDisposable
{
    private sealed record Entry(XamlCompilationSession Compiler, LspDocumentSetSnapshot Buffers, CancellationTokenSource Cancellation, Task<ImmutableArray<XamlAnalysis>> Task);
    private readonly object _gate = new();
    private readonly CancellationToken _lifetime;
    private readonly List<Entry> _entries = new();
    private bool _disposed;
    private long _computations;
    public LspProjectAnalysisCache(CancellationToken lifetime = default) => _lifetime = lifetime;
    public long ComputationCount => Interlocked.Read(ref _computations);

    public Task<ImmutableArray<XamlAnalysis>> GetAsync(XamlCompilationSession compiler, LspDocumentSetSnapshot buffers, CancellationToken waiter = default)
    {
        waiter.ThrowIfCancellationRequested();
        Entry entry;
        Entry? retired = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = _entries.FirstOrDefault(e => ReferenceEquals(e.Compiler, compiler) && e.Buffers.Revision == buffers.Revision && e.Buffers.Documents.SequenceEqual(buffers.Documents))!;
            if (entry == null)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
                Interlocked.Increment(ref _computations);
                var task = Task.Run(() => compiler.AnalyzeWorkspace(buffers.Documents.Select(d => d.Syntax), cancellation.Token), cancellation.Token);
                entry = new(compiler, buffers, cancellation, task);
                _entries.Add(entry);
                if (_entries.Count > 2) { retired = _entries[0]; _entries.RemoveAt(0); }
            }
        }
        if (retired != null) Retire(retired);
        return entry.Task.WaitAsync(waiter);
    }
    public void Invalidate()
    {
        Entry[] retired;
        lock (_gate) { retired = _entries.ToArray(); _entries.Clear(); }
        foreach (var entry in retired) Retire(entry);
    }
    private static void Retire(Entry entry)
    {
        try { entry.Cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { /* Callback failures do not prevent task observation/disposal. */ }
        _ = entry.Task.ContinueWith(task => { _ = task.Exception; entry.Cancellation.Dispose(); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async ValueTask DisposeAsync()
    {
        Entry[] retired;
        lock (_gate) { if (_disposed) return; _disposed = true; retired = _entries.ToArray(); _entries.Clear(); }
        foreach (var entry in retired) Retire(entry);
        try { await Task.WhenAll(retired.Select(e => e.Task)).ConfigureAwait(false); } catch { /* Work failures were delivered to their waiters; disposal only retires resources. */ }
    }
}
