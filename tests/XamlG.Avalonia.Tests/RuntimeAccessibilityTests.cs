using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Inspection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeAccessibilityTests
{
    [AvaloniaFact]
    public async Task Actual_control_peers_expose_metadata_and_invoke_toggle_value_and_range_providers()
    {
        var button = new Button { Name = "run", Content = "Run" }; var clicks = 0; button.Click += (_, _) => clicks++;
        var toggle = new CheckBox { Name = "flag", Content = "Enabled" };
        var box = new TextBox { Name = "entry", Text = "before" };
        var slider = new Slider { Name = "level", Minimum = 0, Maximum = 100, Value = 5 };
        var root = new StackPanel { Children = { button, toggle, box, slider } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var buttonPeer = Peer(inspector, "run"); var togglePeer = Peer(inspector, "flag");
            var textPeer = Peer(inspector, "entry"); var sliderPeer = Peer(inspector, "level");
            Assert.Equal("Run", buttonPeer.Properties["name"].Value);
            Assert.Contains(typeof(IInvokeProvider).FullName!, buttonPeer.Providers);
            Assert.Equal(buttonPeer.Id, Peer(inspector, "run").Id);
            await Invoke(inspector, buttonPeer.Id, "IInvokeProvider", "Invoke()"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, clicks);
            await Invoke(inspector, togglePeer.Id, "IToggleProvider", "Toggle()"); Assert.True(toggle.IsChecked);
            await Invoke(inspector, textPeer.Id, "IValueProvider", "SetValue(System.String)", Literal("after")); Assert.Equal("after", box.Text);
            await Invoke(inspector, sliderPeer.Id, "IRangeValueProvider", "SetValue(System.Double)", Literal(37d)); Assert.Equal(37, slider.Value);
            var provider = inspector.AccessibilityProvider(sliderPeer.Id, "IRangeValueProvider");
            Assert.Contains(provider.Members, member => member.Name == "Value" && Equals(member.Value?.Value, 37d));
            inspector.AccessibilityAction(textPeer.Id, RuntimeAccessibilityAction.Focus, inspector.Revision);
            Assert.True(box.IsFocused);
            Assert.Contains(inspector.Changes().Changes, change => change.Kind == "accessibility");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Provider_calls_preserve_framework_semantics_and_reject_stale_cancelled_or_retired_targets()
    {
        var button = new Button { Name = "run", Content = "Run" }; var clicks = 0; button.Click += (_, _) => clicks++;
        var box = new TextBox { Name = "entry", Text = "before", IsReadOnly = true };
        var root = new StackPanel { Children = { button, box } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root);
            var buttonPeer = Peer(inspector, "run"); var textPeer = Peer(inspector, "entry"); var revision = inspector.Revision;
            button.Content = "Changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.InvokeAccessibilityProviderAsync(buttonPeer.Id, "IInvokeProvider", "Invoke()", [], revision, TestContext.Current.CancellationToken));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InvokeAccessibilityProviderAsync(buttonPeer.Id, "IInvokeProvider", "Invoke()", [], inspector.Revision, cancelled.Token));
            var native = ControlAutomationPeer.CreatePeerForElement(box).GetProvider<IValueProvider>()!;
            Assert.True(native.IsReadOnly);
            // Avalonia permits its provider's programmatic SetValue even when user
            // editing is read-only. The inspector exposes that actual contract.
            native.SetValue("native"); Assert.Equal("native", box.Text);
            var described = inspector.AccessibilityProvider(textPeer.Id, "IValueProvider");
            Assert.Contains(described.Members, member => member.Name == "IsReadOnly" && Equals(member.Value?.Value, true));
            await Invoke(inspector, textPeer.Id, "IValueProvider", "SetValue(System.String)", Literal("changed"));
            Assert.Equal("changed", box.Text); Assert.Equal(0, clicks);
            root.Children.Remove(button); window.UpdateLayout();
            Assert.Throws<KeyNotFoundException>(() => inspector.AccessibilityProvider(buttonPeer.Id, "IInvokeProvider"));
            Assert.Throws<ArgumentException>(() => inspector.AccessibilityProvider(textPeer.Id, "UnknownProvider"));
            Assert.Throws<ArgumentException>(() => inspector.AccessibilityAction(textPeer.Id, (RuntimeAccessibilityAction)100, inspector.Revision));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Selection_provider_arrays_remain_inspectable_and_accept_peer_arguments()
    {
        var first = new ListBoxItem { Content = "one", Name = "first" }; var second = new ListBoxItem { Content = "two", Name = "second" };
        var list = new ListBox { Items = { first, second }, SelectedIndex = 0 }; var window = Show(list);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(list);
            var tree = inspector.Accessibility(); var selection = await Invoke(inspector, tree.RootId, "ISelectionProvider", "GetSelection()");
            var arrayId = Assert.IsType<string>(selection.ObjectId); var peerId = Assert.Single(Assert.IsType<string[]>(selection.Value));
            var entry = Assert.Single(inspector.InspectObject(arrayId).Members);
            Assert.Equal(peerId, entry.Value!.ObjectId); Assert.Equal("accessibility", entry.Value.ReferenceKind);
            var peer = ControlAutomationPeer.CreatePeerForElement(first); var consumer = new PeerConsumer(peer);
            list.DataContext = consumer;
            var result = await inspector.InvokeMethodAsync(inspector.Capture().RootId, ["DataContext"], "IsSame(Avalonia.Automation.Peers.AutomationPeer)",
                [new(ObjectId: peerId)], inspector.Revision, TestContext.Current.CancellationToken);
            Assert.Equal(true, result.Value);
            var secondPeer = Peer(inspector, "second");
            await Invoke(inspector, secondPeer.Id, "ISelectionItemProvider", "Select()"); Assert.Same(second, list.SelectedItem);
            Assert.Equal(peerId, inspector.ReadObject(arrayId, ["0"]).ObjectId);
            inspector.ReleaseObjectHandles([arrayId]); Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(arrayId));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Scroll_provider_changes_the_actual_scrollviewer_offset()
    {
        var scroll = new ScrollViewer { Content = new Border { Width = 900, Height = 1600 },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = Show(scroll);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(scroll); var tree = inspector.Accessibility();
            Assert.Contains(typeof(IScrollProvider).FullName!, tree.Nodes[0].Providers);
            await Invoke(inspector, tree.RootId, "IScrollProvider", "SetScrollPercent(System.Double,System.Double)", Literal(50d), Literal(25d));
            Assert.True(scroll.Offset.X > 0); Assert.True(scroll.Offset.Y > 0);
            var provider = inspector.AccessibilityProvider(tree.RootId, "IScrollProvider");
            Assert.Contains(provider.Members, member => member.Name == "HorizontallyScrollable" && Equals(member.Value?.Value, true));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Returned_objects_retire_when_their_accessibility_origin_leaves_the_tree()
    {
        var list = new ListBox { Name = "items", Items = { "one", "two" }, SelectedIndex = 0 };
        var root = new StackPanel { Children = { list } }; var window = Show(root);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(root); var peer = Peer(inspector, "items");
            var result = await Invoke(inspector, peer.Id, "ISelectionProvider", "GetSelection()");
            var lease = Assert.IsType<string>(result.ObjectId);
            Assert.Contains(inspector.ObjectHandles().Handles, item => item.Id == lease && item.OriginId == peer.Id);
            root.Children.Clear(); window.UpdateLayout();
            Assert.DoesNotContain(inspector.ObjectHandles().Handles, item => item.Id == lease);
            Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(lease));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Virtual_peers_have_stable_paged_identity_relations_and_retire_event_observers()
    {
        var host = new PeerHost(); var window = Show(host);
        try
        {
            using var inspector = new AvaloniaRuntimeInspector(host);
            var parent = Assert.IsType<HostPeer>(ControlAutomationPeer.CreatePeerForElement(host));
            var one = new VirtualPeer("one"); var two = new VirtualPeer("two") { Label = one };
            parent.Replace([one, two]);
            var first = inspector.Accessibility(count: 1); Assert.Equal(3, first.Total); Assert.True(first.HasMore);
            var second = inspector.Accessibility(1, 1); var oneNode = Assert.Single(second.Nodes);
            Assert.Null(oneNode.ObjectId); Assert.Equal(first.RootId, oneNode.ParentId); Assert.Equal("one", oneNode.Properties["name"].Value);
            var twoNode = Assert.Single(inspector.Accessibility(2, 1).Nodes);
            Assert.Equal(oneNode.Id, twoNode.Properties["labeledBy"].ObjectId);
            await Invoke(inspector, oneNode.Id, "IInvokeProvider", "Invoke()"); Assert.Equal(1, one.Invocations);
            Assert.Equal(oneNode.Id, Assert.Single(inspector.Accessibility(1, 1).Nodes).Id);
            var sequence = inspector.Changes().Sequence;
            one.ChangeName("renamed");
            Assert.Contains(inspector.Changes(sequence).Changes, change => change.Kind == "accessibility" && change.Name == "property");
            parent.Replace([two]); inspector.Accessibility();
            Assert.Throws<KeyNotFoundException>(() => inspector.AccessibilityProvider(oneNode.Id, "IInvokeProvider"));
            sequence = inspector.Changes().Sequence; one.ChangeName("retired"); Assert.Empty(inspector.Changes(sequence).Changes);
            inspector.Dispose(); two.ChangeName("after dispose");
        }
        finally { window.Close(); }
    }

    private static RuntimeAccessibilityNode Peer(AvaloniaRuntimeInspector inspector, string name)
    {
        var objectId = inspector.Capture().Nodes.Single(node => node.Name == name).Id;
        return inspector.Accessibility().Nodes.Single(node => node.ObjectId == objectId);
    }
    private static Task<RuntimeValue> Invoke(AvaloniaRuntimeInspector inspector, string peer, string provider, string signature, params RuntimeArgument[] arguments)
    {
        Assert.Contains(signature, inspector.AccessibilityProvider(peer, provider).Methods);
        return inspector.InvokeAccessibilityProviderAsync(peer, provider, signature, arguments, inspector.Revision, TestContext.Current.CancellationToken);
    }
    private static RuntimeArgument Literal(object value) => new(JsonSerializer.SerializeToElement(value));
    private static Window Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); return window;
    }
    public sealed class PeerConsumer(AutomationPeer expected)
    { public bool IsSame(AutomationPeer peer) => ReferenceEquals(expected, peer); }
    private sealed class PeerHost : Control
    { protected override AutomationPeer OnCreateAutomationPeer() => new HostPeer(this); }
    private sealed class HostPeer(Control owner) : ControlAutomationPeer(owner)
    {
        private AutomationPeer[] _children = [];
        public void Replace(AutomationPeer[] children) { _children = children; InvalidateChildren(); }
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => _children;
    }
    private sealed class VirtualPeer(string name) : AutomationPeer, IInvokeProvider
    {
        private AutomationPeer? _parent; private string _name = name;
        public int Invocations { get; private set; }
        public AutomationPeer? Label { get; init; }
        public void Invoke() => Invocations++;
        public void ChangeName(string name) { var old = _name; _name = name; RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, name); }
        protected override void BringIntoViewCore() { }
        protected override string? GetAcceleratorKeyCore() => null;
        protected override string? GetAccessKeyCore() => null;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string? GetAutomationIdCore() => null;
        protected override Rect GetBoundingRectangleCore() => default;
        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() => [];
        protected override string GetClassNameCore() => "VirtualRow";
        protected override AutomationPeer? GetLabeledByCore() => Label;
        protected override string? GetNameCore() => _name;
        protected override AutomationPeer? GetParentCore() => _parent;
        protected override bool HasKeyboardFocusCore() => false;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => true;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override void SetFocusCore() { }
        protected override bool ShowContextMenuCore() => false;
        protected override bool TrySetParent(AutomationPeer? parent) { _parent = parent; return true; }
    }
}
