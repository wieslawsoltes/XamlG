using System.Runtime.CompilerServices;
using System.Threading;

namespace XamlG.Runtime;

/// <summary>Owns generated-object inspection and reversible setters without rooting application windows globally.</summary>
public sealed class XamlRuntimeSession : IDisposable
{
    private static readonly ConditionalWeakTable<object, XamlRuntimeSession> Sessions = new();
    private readonly Dictionary<string, XamlRuntimeNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _instances = new(XamlObjectIdentityComparer.Instance);
    private readonly Dictionary<(string Node, string Member), XamlRuntimeProperty> _properties = new();
    private readonly List<Action> _cleanup = new();
    private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
    private bool _disposed;
    private bool _applying;
    public long Revision { get; private set; }
    public bool IsDisposed => _disposed;
    public IReadOnlyCollection<XamlRuntimeNode> Nodes => _nodes.Values;

    public static bool TryGet(object root, out XamlRuntimeSession? session)
    {
        if (Sessions.TryGetValue(root, out session) && !session._disposed) return true;
        session = null; return false;
    }
    public void Attach(object root)
    {
        CheckThread();
        if (Sessions.TryGetValue(root, out var previous) && !ReferenceEquals(previous, this))
        {
            if (!XamlConstructionScope.AdoptPreviousSession(root, this, previous)) previous.Dispose();
            Sessions.Remove(root);
        }
        if (!Sessions.TryGetValue(root, out _)) Sessions.Add(root, this);
    }
    public void Register(string key, object instance, string? parentKey)
    {
        CheckThread();
        if (_nodes.TryGetValue(key, out var old) && !ReferenceEquals(old.Instance, instance))
            throw new InvalidOperationException($"Duplicate generated node key '{key}'.");
        _nodes[key] = new(key, instance, parentKey);
        if (!_instances.ContainsKey(instance)) _instances.Add(instance, key);
    }
    public void RegisterSource(string key, XamlSourceInfo source)
    {
        CheckThread();
        if (!_nodes.TryGetValue(key, out var node)) throw new ArgumentException("The source mapping requires an existing generated node.", nameof(key));
        _nodes[key] = node with { Source = source ?? throw new ArgumentNullException(nameof(source)) };
    }
    public XamlRuntimeNode? FindNode(object instance)
    {
        CheckThread();
        return _instances.TryGetValue(instance, out var key) ? _nodes[key] : null;
    }
    public void RegisterProperty<T>(string key, string member, Func<T> getter, Action<T> setter)
    {
        CheckThread(); _properties[(key, member)] = new(typeof(T), () => getter(), value => setter((T)value!));
    }
    public void TrackCleanup(Action action)
    {
        CheckThread(); _cleanup.Add(action ?? throw new ArgumentNullException(nameof(action)));
    }
    public XamlMutationResult Apply(long expectedRevision, IReadOnlyList<XamlPropertyUpdate> updates)
    {
        CheckThread();
        if (expectedRevision != Revision) return new(false, Revision, "The live view has a different revision.");
        if (_applying) return new(false, Revision, "A reentrant update is not permitted.");
        var pending = new List<(XamlRuntimeProperty Property, object? Before, object? After)>();
        var keys = new HashSet<(string, string)>();
        foreach (var update in updates)
        {
            if (!keys.Add((update.NodeKey, update.MemberName))) return new(false, Revision, "The batch assigns a property more than once.");
            if (!_properties.TryGetValue((update.NodeKey, update.MemberName), out var property)) return new(false, Revision, $"'{update.NodeKey}.{update.MemberName}' requires a structural rebuild.");
            if (!property.Accepts(update.Value)) return new(false, Revision, $"The replacement value is not assignable to '{property.Type}'.");
            try { pending.Add((property, property.Get(), update.Value)); }
            catch (Exception error) { return new(false, Revision, "A property getter failed: " + error.Message); }
        }
        if (pending.Count == 0) return new(true, Revision, null);
        var nextRevision = checked(Revision + 1);
        _applying = true; var applied = 0;
        try
        {
            for (; applied < pending.Count; applied++) pending[applied].Property.Set(pending[applied].After);
            Revision = nextRevision; return new(true, Revision, null);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            for (var i = Math.Min(applied, pending.Count - 1); i >= 0; i--)
                try { pending[i].Property.Set(pending[i].Before); } catch (Exception rollback) { failures.Add(rollback); }
            if (failures.Count > 1) throw new AggregateException("The update and at least one rollback operation failed; rebuild the view.", failures);
            return new(false, Revision, error.Message);
        }
        finally { _applying = false; }
    }
    public void Dispose()
    {
        if (_disposed) return; CheckThread(); _disposed = true;
        var errors = new List<Exception>();
        for (var i = _cleanup.Count - 1; i >= 0; i--)
            try { _cleanup[i](); } catch (Exception error) { errors.Add(error); }
        _cleanup.Clear(); _properties.Clear(); _instances.Clear(); _nodes.Clear();
        if (errors.Count != 0) throw new AggregateException("Generated event cleanup failed.", errors);
    }
    private void CheckThread()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(XamlRuntimeSession));
        if (Thread.CurrentThread.ManagedThreadId != _threadId) throw new InvalidOperationException("Generated view mutations must run on the view's owning thread.");
    }
}
