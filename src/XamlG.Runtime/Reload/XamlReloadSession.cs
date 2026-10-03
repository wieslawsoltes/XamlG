using System.Threading;

namespace XamlG.Runtime.Reload;

/// <summary>Builds a detached replacement, transfers eligible state, publishes once and retires the old graph.
/// This is structural replacement with state transfer, not arbitrary object-identity-preserving reconciliation.</summary>
public sealed class XamlReloadSession
{
    private readonly IXamlStateTransferAdapter[] _adapters;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private bool _applying;
    public XamlReloadSession(object root, IEnumerable<IXamlStateTransferAdapter>? adapters = null)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        _adapters = adapters?.ToArray() ?? Array.Empty<IXamlStateTransferAdapter>();
    }
    public object Root { get; private set; }
    public long Revision { get; private set; }

    public XamlReloadResult Reload(long expectedRevision, Func<object> build, Action<object> publish)
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Reload must run on the view's owning thread.");
        if (build == null) throw new ArgumentNullException(nameof(build));
        if (publish == null) throw new ArgumentNullException(nameof(publish));
        var warnings = new List<string>();
        if (_applying || expectedRevision != Revision) return new(false, Revision, Root, "The reload is stale or reentrant.", warnings);
        var nextRevision = checked(Revision + 1);
        var previous = Root;
        object? candidate = null;
        var published = false;
        var operations = new List<XamlStateTransferOperation>();
        XamlRuntimeSession.TryGet(previous, out var oldSession);
        XamlRuntimeSession? newSession = null;
        _applying = true;
        try
        {
            candidate = build() ?? throw new InvalidOperationException("The replacement factory returned null.");
            if (ReferenceEquals(previous, candidate)) throw new InvalidOperationException("Structural reload requires a detached replacement root.");
            XamlRuntimeSession.TryGet(candidate, out newSession);
            if (oldSession != null && newSession != null)
                foreach (var pair in XamlNodeMatcher.Match(oldSession, newSession, previous, candidate))
                    foreach (var adapter in _adapters)
                        if (adapter.Prepare(pair) is { } operation) operations.Add(operation);
            foreach (var operation in operations) operation.Apply();
            try { publish(candidate); }
            catch (Exception error)
            {
                try { publish(previous); }
                catch (Exception rollback) { throw new AggregateException("Publishing the candidate and restoring the previous root both failed.", error, rollback); }
                throw;
            }
            Root = candidate;
            Revision = nextRevision;
            published = true;
            foreach (var operation in operations)
                try { operation.AfterCommit?.Invoke(); } catch (Exception error) { warnings.Add("Post-reload state: " + error.Message); }
            try { oldSession?.Dispose(); } catch (Exception error) { warnings.Add("Retired graph cleanup: " + error.Message); }
            return new(true, Revision, Root, null, warnings.ToArray());
        }
        catch (AggregateException error) when (!published && error.InnerExceptions.Count > 1)
        {
            try { newSession?.Dispose(); } catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
        catch (Exception error)
        {
            if (!published && candidate != null && !ReferenceEquals(candidate, previous))
                try { newSession?.Dispose(); } catch (Exception cleanup) { warnings.Add("Candidate cleanup: " + cleanup.Message); }
            return new(false, Revision, Root, error.Message, warnings.ToArray());
        }
        finally { _applying = false; }
    }
}
