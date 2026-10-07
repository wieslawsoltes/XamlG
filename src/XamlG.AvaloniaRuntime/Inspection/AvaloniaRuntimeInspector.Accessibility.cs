using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private readonly ConditionalWeakTable<AutomationPeer, ObjectIdentity> _accessibilityIds = new();
    private readonly Dictionary<string, AutomationPeer> _accessibilityPeers = new(StringComparer.Ordinal);
    private readonly Dictionary<AutomationPeer, Action> _accessibilityCleanup = new(ReferenceEqualityComparer.Instance);
    private long _nextAccessibilityId;
    private string _accessibilityTopology = "";
    private static readonly Type[] AccessibilityProviderTypes = typeof(IInvokeProvider).Assembly.GetExportedTypes()
        .Where(type => type.IsInterface && type.Namespace == typeof(IInvokeProvider).Namespace).OrderBy(TypeName, StringComparer.Ordinal).ToArray();
    private static readonly MethodInfo GetAccessibilityProvider = typeof(AutomationPeer).GetMethod(nameof(AutomationPeer.GetProvider))!;

    /// <summary>Reads the actual automation-peer tree, including virtual peers which
    /// have no corresponding visual. Stable peer handles retire when that tree changes.</summary>
    public RuntimeAccessibilitySnapshot Accessibility(int offset = 0, int count = 100)
    {
        if (offset is < 0 or > 100000 || count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(offset));
        var tree = CaptureAccessibility();
        var nodes = tree.Skip(offset).Take(count).Select(entry => DescribeAccessibility(entry.Peer, entry.Parent, entry.Children, entry.Error)).ToArray();
        return new(SessionId, Revision, PeerId(tree[0].Peer), tree.Count, offset, offset + nodes.Length < tree.Count, nodes);
    }

    public RuntimeAccessibilityProvider AccessibilityProvider(string peerId, string providerName)
    {
        var peer = ResolveAccessibility(peerId); var (type, provider) = ResolveAccessibilityProvider(peer, providerName);
        var interfaces = type.GetInterfaces().Prepend(type).ToArray();
        var members = interfaces.SelectMany(item => item.GetProperties()).Where(property => property.GetMethod != null && property.GetIndexParameters().Length == 0)
            .DistinctBy(property => property.Name).OrderBy(property => property.Name, StringComparer.Ordinal).Take(128).Select(property =>
            {
                try { return new RuntimeMember(property.Name, TypeName(property.PropertyType), "property", property.SetMethod == null, DescribeAccessibilityValue(property.GetValue(provider), peerId)); }
                catch (Exception error) { return new RuntimeMember(property.Name, TypeName(property.PropertyType), "property", true, null, ErrorText(error)); }
            }).ToArray();
        var methods = interfaces.SelectMany(item => item.GetMethods()).Where(Callable).Select(Signature).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(128).ToArray();
        return new(Revision, peerId, TypeName(type), members, methods, DescribeObjectValue(provider, peerId));
    }

    public async Task<RuntimeValue> InvokeAccessibilityProviderAsync(string peerId, string providerName, string signature,
        IReadOnlyList<RuntimeArgument> arguments, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var peer = ResolveAccessibility(peerId, expectedRevision); var (type, provider) = ResolveAccessibilityProvider(peer, providerName);
        if (arguments.Count > 32) throw new ArgumentException("At most 32 provider arguments are supported.");
        var method = type.GetInterfaces().Prepend(type).SelectMany(item => item.GetMethods()).Where(Callable).DistinctBy(Signature)
            .SingleOrDefault(method => Signature(method) == signature) ?? throw new ArgumentException("Use an exact signature returned by provider inspection.");
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Count) throw new ArgumentException("The provider argument count does not match.");
        var values = parameters.Select((parameter, index) => ConvertArgument(arguments[index], parameter.ParameterType)).ToArray();
        ResolveAccessibility(peerId, expectedRevision); cancellationToken.ThrowIfCancellationRequested();
        object? result;
        try { result = method.Invoke(provider, values); }
        catch (TargetInvocationException error) { throw new InvalidOperationException(ErrorText(error), error.InnerException); }
        Changed(Id(_root), "accessibility", providerName + "." + signature, null);
        if (result is ValueTask valueTask) result = valueTask.AsTask();
        else if (result != null && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            result = method.ReturnType.GetMethod(nameof(ValueTask.AsTask))!.Invoke(result, null);
        if (result is Task task)
        {
            await task.WaitAsync(cancellationToken); VerifyAccess();
            result = method.ReturnType.IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
        }
        CaptureAccessibility();
        return DescribeAccessibilityValue(result, peerId);
    }

    public RuntimeValue AccessibilityAction(string peerId, RuntimeAccessibilityAction action, long expectedRevision)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown accessibility action.");
        var peer = ResolveAccessibility(peerId, expectedRevision); object? result = null;
        switch (action)
        {
            case RuntimeAccessibilityAction.Focus: peer.SetFocus(); break;
            case RuntimeAccessibilityAction.BringIntoView: peer.BringIntoView(); break;
            case RuntimeAccessibilityAction.ContextMenu: result = peer.ShowContextMenu(); break;
        }
        Changed(Id(_root), "accessibility", action.ToString(), null); return DescribeValue(result);
    }

    private List<AccessibilityEntry> CaptureAccessibility()
    {
        Capture();
        var root = _root as Control ?? throw new InvalidOperationException("Accessibility inspection requires a control root.");
        var seen = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        var entries = new List<AccessibilityEntry>();
        var pending = new Queue<(AutomationPeer Peer, string? Parent, int Depth)>();
        pending.Enqueue((ControlAutomationPeer.CreatePeerForElement(root), null, 0));
        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next.Peer)) continue;
            if (seen.Count > MaximumNodes || next.Depth > MaximumDepth) throw new InvalidOperationException("The accessibility tree exceeds the configured inspection limits.");
            var children = Array.Empty<AutomationPeer>(); string? error = null;
            try { children = next.Peer.GetChildren().Take(MaximumNodes + 1).ToArray(); }
            catch (Exception failure) { error = ErrorText(failure); }
            if (children.Length > MaximumNodes) throw new InvalidOperationException("Too many accessibility children.");
            entries.Add(new(next.Peer, next.Parent, children.Select(PeerId).ToArray(), error));
            foreach (var child in children) pending.Enqueue((child, PeerId(next.Peer), next.Depth + 1));
        }
        foreach (var old in _accessibilityCleanup.Keys.Where(peer => !seen.Contains(peer)).ToArray())
        { _accessibilityCleanup[old](); _accessibilityCleanup.Remove(old); RetireObjectWatches(PeerId(old)); RetireObjectHandles(PeerId(old)); }
        _accessibilityPeers.Clear();
        foreach (var entry in entries)
        {
            _accessibilityPeers.Add(PeerId(entry.Peer), entry.Peer);
            if (_accessibilityCleanup.ContainsKey(entry.Peer)) continue;
            EventHandler childrenChanged = (_, _) => Changed(Id(_root), "accessibility", "children", null);
            EventHandler<AutomationPropertyChangedEventArgs> propertyChanged = (_, _) => Changed(Id(_root), "accessibility", "property", null);
            entry.Peer.ChildrenChanged += childrenChanged; entry.Peer.PropertyChanged += propertyChanged;
            _accessibilityCleanup.Add(entry.Peer, () => { entry.Peer.ChildrenChanged -= childrenChanged; entry.Peer.PropertyChanged -= propertyChanged; });
        }
        var topology = string.Join('|', entries.Select(entry => PeerId(entry.Peer) + ":" + entry.Parent));
        if (_accessibilityTopology != topology)
        { _accessibilityTopology = topology; Changed(Id(_root), "accessibility", "tree", null); }
        return entries;
    }

    private RuntimeAccessibilityNode DescribeAccessibility(AutomationPeer peer, string? parent, string[] children, string? treeError)
    {
        var values = new Dictionary<string, RuntimeValue>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        void Read(string name, Func<object?> get)
        { try { values[name] = DescribeAccessibilityValue(get(), PeerId(peer)); } catch (Exception error) { errors[name] = ErrorText(error); } }
        Read("name", peer.GetName); Read("automationId", peer.GetAutomationId); Read("className", peer.GetClassName);
        Read("controlType", () => peer.GetAutomationControlType().ToString()); Read("helpText", peer.GetHelpText); Read("placeholder", peer.GetPlaceholderText);
        Read("enabled", () => peer.IsEnabled()); Read("offscreen", () => peer.IsOffscreen());
        Read("keyboardFocusable", () => peer.IsKeyboardFocusable()); Read("keyboardFocus", () => peer.HasKeyboardFocus());
        Read("contentElement", () => peer.IsContentElement()); Read("controlElement", () => peer.IsControlElement());
        Read("bounds", () => peer.GetBoundingRectangle()); Read("acceleratorKey", peer.GetAcceleratorKey); Read("accessKey", peer.GetAccessKey);
        Read("itemType", peer.GetItemType); Read("itemStatus", peer.GetItemStatus); Read("headingLevel", () => peer.GetHeadingLevel());
        Read("landmarkType", () => peer.GetLandmarkType()?.ToString()); Read("liveSetting", () => peer.GetLiveSetting().ToString()); Read("labeledBy", peer.GetLabeledBy);
        var providers = new List<string>();
        foreach (var type in AccessibilityProviderTypes)
            try { if (GetAccessibilityProvider.MakeGenericMethod(type).Invoke(peer, null) != null) providers.Add(TypeName(type)); }
            catch (Exception error) { errors[TypeName(type)] = ErrorText(error); }
        if (treeError != null) errors["children"] = treeError;
        var objectId = peer is ControlAutomationPeer control && _objects.ContainsKey(Id(control.Owner)) ? Id(control.Owner) : null;
        return new(PeerId(peer), objectId, parent, children, values, providers, errors);
    }
    private AutomationPeer ResolveAccessibility(string id, long? expectedRevision = null)
    {
        CaptureAccessibility();
        if (expectedRevision != null && Revision != expectedRevision) throw new InvalidOperationException("Runtime revision changed. Inspect accessibility again before editing.");
        return _accessibilityPeers.TryGetValue(id, out var peer) ? peer : throw new KeyNotFoundException("The accessibility handle is stale or unknown.");
    }
    private static (Type Type, object Provider) ResolveAccessibilityProvider(AutomationPeer peer, string name)
    {
        var type = AccessibilityProviderTypes.SingleOrDefault(type => TypeName(type) == name || type.Name == name)
            ?? throw new ArgumentException("Unknown automation provider interface.");
        return (type, GetAccessibilityProvider.MakeGenericMethod(type).Invoke(peer, null) ?? throw new InvalidOperationException("This peer no longer supports that provider."));
    }
    private string PeerId(AutomationPeer peer) => _accessibilityIds.GetValue(peer, _ => new(SessionId + ":a" + ++_nextAccessibilityId)).Id;
    private RuntimeValue DescribeAccessibilityValue(object? value, string originId)
    {
        if (value is Rect bounds) return new(TypeName(typeof(Rect)), new { bounds.X, bounds.Y, bounds.Width, bounds.Height });
        if (value is AutomationPeer peer)
            return DescribeObjectValue(value, originId) with { Value = new { peerId = _accessibilityPeers.ContainsKey(PeerId(peer)) ? PeerId(peer) : null } };
        if (value is IEnumerable<AutomationPeer> peers)
        {
            var ids = peers.Take(501).Select(peer => _accessibilityPeers.ContainsKey(PeerId(peer)) ? PeerId(peer) : null).ToArray();
            return DescribeObjectValue(value, originId) with { Value = ids.Take(500).ToArray(), Truncated = ids.Length > 500 };
        }
        return DescribeObjectValue(value, originId);
    }
    private void DisposeAccessibility()
    {
        foreach (var cleanup in _accessibilityCleanup.Values) cleanup();
        _accessibilityCleanup.Clear(); _accessibilityPeers.Clear();
    }
    private sealed record AccessibilityEntry(AutomationPeer Peer, string? Parent, string[] Children, string? Error);
}
