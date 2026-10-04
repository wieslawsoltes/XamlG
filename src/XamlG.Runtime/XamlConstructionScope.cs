using System.Threading;

namespace XamlG.Runtime;

/// <summary>Supplies services to a generated initializer called by a synchronous code-behind
/// constructor. Scopes are thread-local, stack-disciplined and never flow into background tasks.
/// Failed constructors retire every generated graph initialized within their matching scope.</summary>
public sealed class XamlConstructionScope : IDisposable
{
    [ThreadStatic] private static XamlConstructionScope? _current;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Type _rootType;
    private readonly List<XamlRuntimeSession> _sessions = new();
    private readonly HashSet<XamlRuntimeSession> _adopted = new();
    private XamlConstructionScope? _parent;
    private IServiceProvider? _services;
    private bool _disposed;
    private bool _committed;

    private XamlConstructionScope(Type rootType, IServiceProvider? services)
    {
        _rootType = rootType; _services = services; _parent = _current; _current = this;
    }
    public static XamlConstructionScope Begin(Type rootType, IServiceProvider? services) =>
        new(rootType ?? throw new ArgumentNullException(nameof(rootType)), services);

    public static IServiceProvider? GetServices(Type rootType) =>
        _current is { _disposed: false } scope && scope._rootType == rootType ? scope._services : null;

    /// <summary>Provides services to base and derived generated initializers on the actual
    /// factory instance, without providing them to unrelated instances of its base type.</summary>
    public static IServiceProvider? GetServicesFor(object root) =>
        _current is { _disposed: false } scope && root != null && scope._rootType == root.GetType() ? scope._services : null;

    internal static bool AdoptPreviousSession(object root, XamlRuntimeSession successor, XamlRuntimeSession previous)
    {
        var scope = _current;
        if (scope == null || scope._disposed || scope._committed || scope._rootType != root.GetType() || previous.IsDisposed) return false;
        scope.CheckCurrent();
        // Derived initialization must not retire bindings installed by a base constructor.
        // The successor owns the predecessor; abort still disposes all recorded layers.
        successor.TrackCleanup(previous.Dispose);
        scope._adopted.Add(previous);
        return true;
    }

    /// <summary>Called only after a generated initializer has successfully attached its session.</summary>
    public static void RegisterInitialized(Type rootType, object root)
    {
        var scope = _current;
        if (scope == null || scope._disposed || root == null || scope._rootType != root.GetType() || !rootType.IsInstanceOfType(root)) return;
        scope.CheckCurrent();
        if (XamlRuntimeSession.TryGet(root, out var session) && !scope._sessions.Contains(session!)) scope._sessions.Add(session!);
    }

    /// <summary>Transfers constructed graphs to the returned root rather than a global registry.</summary>
    public void Commit(object root)
    {
        CheckCurrent();
        if (_committed) throw new InvalidOperationException("The construction scope is already committed.");
        if (!XamlRuntimeSession.TryGet(root, out var owner)) throw new InvalidOperationException("The factory root has not been initialized.");
        foreach (var session in _sessions)
            if (!ReferenceEquals(owner, session) && !session.IsDisposed && !_adopted.Contains(session)) owner!.TrackCleanup(session.Dispose);
        _sessions.Clear(); _adopted.Clear(); _committed = true;
    }

    /// <summary>Preserves the constructor exception when cleanup also throws. The caller rethrows
    /// its original exception when cleanup succeeds; disposal after Abort is idempotent.</summary>
    public void Abort(Exception constructionFailure)
    {
        if (constructionFailure == null) throw new ArgumentNullException(nameof(constructionFailure));
        var failures = Finish();
        if (failures.Count != 0)
        {
            failures.Insert(0, constructionFailure);
            throw new AggregateException("Code-behind construction and cleanup both failed.", failures);
        }
    }
    public void Dispose()
    {
        var failures = Finish();
        if (failures.Count != 0) throw new AggregateException("Code-behind construction cleanup failed.", failures);
    }
    private List<Exception> Finish()
    {
        var errors = new List<Exception>();
        if (_disposed) return errors;
        CheckCurrent();
        // Restore the caller before cleanup: user cleanup may construct another XAML graph.
        _current = _parent; _parent = null; _services = null; _disposed = true;
        if (!_committed)
            for (var index = _sessions.Count - 1; index >= 0; index--)
                try { _sessions[index].Dispose(); } catch (Exception error) { errors.Add(error); }
        _sessions.Clear(); _adopted.Clear();
        return errors;
    }
    private void CheckCurrent()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XamlConstructionScope));
        if (_thread != Thread.CurrentThread.ManagedThreadId || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("Synchronous construction scopes must complete on their creating thread in stack order.");
    }
}
