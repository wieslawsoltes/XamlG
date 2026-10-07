using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Threading;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private const int MaximumObjectHandles = 512;
    private static readonly TimeSpan ObjectHandleLifetime = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, RetainedObject> _objectHandles = new(StringComparer.Ordinal);
    private DispatcherTimer? _objectHandleTimer;
    private long _nextObjectHandle;

    /// <summary>Lists bounded leases for non-tree objects returned by explicit reads and
    /// method calls. A lease retains identity, not a path whose value may later change.</summary>
    public RuntimeObjectHandles ObjectHandles()
    {
        Capture();
        if (_objectHandles.Values.Any(entry => IsPeerHandle(entry.OriginId))) CaptureAccessibility();
        ExpireObjectHandles();
        return new(Revision, MaximumObjectHandles, _objectHandles.Select(pair =>
            new RuntimeObjectHandle(pair.Key, TypeName(pair.Value.Value.GetType()), pair.Value.OriginId, pair.Value.ExpiresAt)).ToArray());
    }

    /// <summary>Releases inspector references and watches, never disposes application
    /// objects. Null releases all leases; tree and accessibility IDs are not leases.</summary>
    public int ReleaseObjectHandles(IReadOnlyList<string>? objectIds = null)
    {
        VerifyAccess();
        if (objectIds?.Count > MaximumObjectHandles || objectIds?.Any(id => id == null || !IsRetainedHandle(id)) == true)
            throw new ArgumentException("Supply retained object IDs from this inspection session, or omit IDs to release all.");
        var ids = objectIds?.Distinct(StringComparer.Ordinal).ToArray() ?? _objectHandles.Keys.ToArray();
        var released = 0;
        foreach (var id in ids) if (RetireObjectHandle(id)) released++;
        return released;
    }

    private object ResolveObjectTarget(string objectId, long? expectedRevision = null)
    {
        VerifyAccess();
        object value;
        if (IsRetainedHandle(objectId))
        {
            ExpireObjectHandles();
            if (!_objectHandles.TryGetValue(objectId, out var entry)) throw StaleObjectHandle();
            // An origin is always a tree node or peer, never another lease. Releasing
            // an intermediate result does not invalidate independently retained children.
            if (IsPeerHandle(entry.OriginId)) ResolveAccessibility(entry.OriginId);
            else Resolve(entry.OriginId);
            if (!_objectHandles.ContainsKey(objectId)) throw StaleObjectHandle();
            value = entry.Value;
            // An object that joined the tree after it was retained must now obey tree
            // lifetime checks as well; a lease cannot revive a detached visual.
            if (value is AvaloniaObject avalonia && _identities.TryGetValue(avalonia, out var identity) && !_objects.ContainsKey(identity.Id))
            { RetireObjectHandle(objectId); throw StaleObjectHandle(); }
        }
        else if (IsPeerHandle(objectId)) value = ResolveAccessibility(objectId);
        else value = Resolve(objectId);
        if (expectedRevision != null && Revision != expectedRevision)
            throw new InvalidOperationException($"Runtime revision conflict: expected {expectedRevision}, actual {Revision}. Inspect again before editing.");
        return value;
    }

    private bool TryKnownObjectTarget(string id, out object? value)
    {
        value = null;
        if (_objects.TryGetValue(id, out var tree)) { value = tree; return true; }
        if (_accessibilityPeers.TryGetValue(id, out var peer)) { value = peer; return true; }
        if (_objectHandles.TryGetValue(id, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow && KnownObjectOrigin(entry.OriginId))
        { value = entry.Value; return true; }
        return false;
    }

    private string ObjectOrigin(string objectId) => _objectHandles.TryGetValue(objectId, out var entry) ? entry.OriginId : objectId;
    private bool KnownObjectOrigin(string id) => _objects.ContainsKey(id) || _accessibilityPeers.ContainsKey(id);
    private bool IsRetainedHandle(string id) => id.StartsWith(SessionId + ":o", StringComparison.Ordinal);
    private bool IsPeerHandle(string id) => id.StartsWith(SessionId + ":a", StringComparison.Ordinal);
    private static KeyNotFoundException StaleObjectHandle() => new("The object handle expired, was released, or its originating tree node/peer was removed. Inspect again.");

    private RuntimeValue DescribeObjectValue(object? value, string originId)
    {
        var description = DescribeValue(value);
        if (value == null || value is string || value.GetType().IsValueType) return description;
        if (description.ObjectId != null) return description;
        if (value is AutomationPeer peer)
        {
            var id = PeerId(peer);
            return _accessibilityPeers.ContainsKey(id)
                ? description with { ObjectId = id, ReferenceKind = "accessibility" }
                : description with { ReferenceError = "The returned peer is outside the current accessibility tree. Refresh accessibility to resolve it." };
        }
        originId = ObjectOrigin(originId);
        if (!KnownObjectOrigin(originId)) return description with { ReferenceError = "The originating tree node or peer is no longer current." };
        if (value is AvaloniaObject avalonia && _identities.TryGetValue(avalonia, out var identity) && !_objects.ContainsKey(identity.Id))
            return description with { ReferenceError = "The returned object has left the inspected tree." };
        ExpireObjectHandles();
        foreach (var pair in _objectHandles)
            if (pair.Value.OriginId == originId && ReferenceEquals(pair.Value.Value, value))
                return description with { ObjectId = pair.Key, ReferenceKind = "object" };
        // Do not evict IDs already returned in the same page or turn. A failed lease
        // allocation must not turn a successful application invocation into a retry.
        if (_objectHandles.Count >= MaximumObjectHandles)
            return description with { ReferenceError = "The 512-object lease limit is reached. Release unneeded object handles and inspect again." };
        var objectId = SessionId + ":o" + ++_nextObjectHandle;
        _objectHandles.Add(objectId, new(value, originId, DateTimeOffset.UtcNow + ObjectHandleLifetime));
        if (_objectHandleTimer == null)
        {
            _objectHandleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _objectHandleTimer.Tick += (_, _) => ExpireObjectHandles();
        }
        _objectHandleTimer.Start();
        return description with { ObjectId = objectId, ReferenceKind = "object" };
    }

    private void ExpireObjectHandles()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _objectHandles.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) RetireObjectHandle(id);
    }
    private bool RetireObjectHandle(string id)
    {
        if (!_objectHandles.Remove(id)) return false;
        RetireObjectWatches(id);
        if (_objectHandles.Count == 0) _objectHandleTimer?.Stop();
        return true;
    }
    private void RetireObjectHandles(string originId)
    {
        foreach (var id in _objectHandles.Where(pair => pair.Value.OriginId == originId).Select(pair => pair.Key).ToArray()) RetireObjectHandle(id);
    }
    private void DisposeObjectHandles()
    {
        _objectHandleTimer?.Stop(); _objectHandleTimer = null;
        ReleaseObjectHandles();
    }

    private static Type[] PublicObjectInterfaces(object target) => target.GetType().GetInterfaces()
        .Where(type => type.IsVisible && !type.ContainsGenericParameters).OrderBy(TypeName, StringComparer.Ordinal).ToArray();
    private static Type? ObjectInterface(object target, string? interfaceName)
    {
        if (interfaceName == null) return null;
        var matches = PublicObjectInterfaces(target).Where(type => TypeName(type) == interfaceName || type.AssemblyQualifiedName == interfaceName).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException("Select an unambiguous public interface returned by object inspection.");
    }
    private static IEnumerable<MethodInfo> ObjectMethods(object target, BindingFlags flags, Type? contract) =>
        (contract == null ? target.GetType().GetMethods(flags) : contract.GetInterfaces().Prepend(contract).SelectMany(type => type.GetMethods()))
            .Where(Callable).DistinctBy(Signature);
    private sealed record RetainedObject(object Value, string OriginId, DateTimeOffset ExpiresAt);
}
