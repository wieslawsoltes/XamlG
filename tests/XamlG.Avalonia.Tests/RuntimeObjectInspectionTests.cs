using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlG.AvaloniaRuntime.Inspection;
using Xunit;

namespace XamlG.Avalonia.Tests;

public sealed class RuntimeObjectInspectionTests
{
    [AvaloniaFact]
    public async Task Retained_objects_preserve_identity_and_support_explicit_public_interfaces()
    {
        ICounter original = new Counter();
        var view = new Border { DataContext = original };
        using var inspector = new AvaloniaRuntimeInspector(view);
        var root = inspector.Capture().RootId;
        Assert.Empty(inspector.ObjectHandles().Handles);
        var value = inspector.ReadObject(root, ["DataContext"]);
        Assert.Equal("object", value.ReferenceKind);
        var id = Assert.IsType<string>(value.ObjectId);
        Assert.Equal(id, inspector.ReadObject(root, ["DataContext"]).ObjectId);
        var contract = typeof(ICounter).AssemblyQualifiedName!;
        var inspected = inspector.InspectObject(id, interfaceName: contract);
        Assert.Contains(contract, inspected.Interfaces!);
        Assert.Contains("Add(System.Int32)", inspected.Methods);
        Assert.Contains(inspected.Members, member => member.Name == "Value" && !member.ReadOnly);
        view.DataContext = new Counter();
        inspector.SetObjectMember(id, ["Value"], Literal(7), inspector.Revision, contract);
        Assert.Equal(7, original.Value); Assert.Equal(0, ((ICounter)view.DataContext).Value);
        var result = await inspector.InvokeMethodAsync(id, null, "Add(System.Int32)", [Literal(3)], inspector.Revision,
            TestContext.Current.CancellationToken, contract);
        Assert.Equal(10, result.Value);
        result = await inspector.InvokeMethodAsync(id, null, "Same(" + typeof(ICounter).FullName + ")", [new(ObjectId: id)], inspector.Revision,
            TestContext.Current.CancellationToken, contract);
        Assert.Equal(true, result.Value);
        Assert.Equal(10, inspector.ReadObject(id, ["Value"], contract).Value);
        Assert.Throws<ArgumentException>(() => inspector.InspectObject(id, interfaceName: typeof(IDisposable).FullName));
    }

    [AvaloniaFact]
    public void Child_leases_survive_parent_release_but_retire_with_their_tree_origin()
    {
        var view = new Border { DataContext = new Counter() }; var panel = new StackPanel { Children = { view } };
        using var inspector = new AvaloniaRuntimeInspector(panel);
        var state = inspector.Capture(); var viewId = state.Nodes.Single(node => node.Type == typeof(Border).FullName).Id;
        var parent = inspector.ReadObject(viewId, ["DataContext"]).ObjectId!;
        var child = inspector.ReadObject(parent, ["Child"], typeof(ICounter).FullName).ObjectId!;
        Assert.Equal(1, inspector.ReleaseObjectHandles([parent, parent]));
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(parent));
        Assert.Equal(0, inspector.ReadObject(child, ["Value"], typeof(ICounter).FullName).Value);
        Assert.Equal(viewId, Assert.Single(inspector.ObjectHandles().Handles).OriginId);
        using var other = new AvaloniaRuntimeInspector(panel);
        Assert.Throws<KeyNotFoundException>(() => other.InspectObject(child));
        panel.Children.Clear(); inspector.Capture();
        Assert.Empty(inspector.ObjectHandles().Handles);
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(child));
        panel.Children.Add(view); inspector.Capture();
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(child));
    }

    [AvaloniaFact]
    public void Lease_expiry_is_absolute_and_reads_do_not_extend_it()
    {
        var clock = new ManualClock(); var view = new Border { DataContext = new Counter() };
        using var inspector = new AvaloniaRuntimeInspector(view, clock);
        var root = inspector.Capture().RootId;
        var id = inspector.ReadObject(root, ["DataContext"]).ObjectId!;
        var expiry = Assert.Single(inspector.ObjectHandles().Handles).ExpiresAt;
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(5), expiry);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(id, inspector.ReadObject(root, ["DataContext"]).ObjectId);
        Assert.Equal(expiry, Assert.Single(inspector.ObjectHandles().Handles).ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(id));
        Assert.Empty(inspector.ObjectHandles().Handles);
        Assert.NotEqual(id, inspector.ReadObject(root, ["DataContext"]).ObjectId);
    }

    [AvaloniaFact]
    public void Child_objects_receive_independent_expiry_even_after_the_parent_lease_expires()
    {
        var clock = new ManualClock(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = new Counter() }, clock);
        var root = inspector.Capture().RootId;
        var parent = inspector.ReadObject(root, ["DataContext"]).ObjectId!;
        clock.Advance(TimeSpan.FromMinutes(4));
        var child = inspector.ReadObject(parent, ["Child"], typeof(ICounter).FullName).ObjectId!;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(parent));
        Assert.Equal(0, inspector.ReadObject(child, ["Value"], typeof(ICounter).FullName).Value);
        Assert.Equal(child, Assert.Single(inspector.ObjectHandles().Handles).Id);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(child));
    }

    [AvaloniaFact]
    public void A_retained_control_cannot_be_revived_after_joining_and_leaving_the_visual_tree()
    {
        var value = new Border { Name = "leased" }; var root = new StackPanel { DataContext = value };
        using var inspector = new AvaloniaRuntimeInspector(root); var id = inspector.Capture().RootId;
        var lease = inspector.ReadObject(id, ["DataContext"]).ObjectId!;
        root.Children.Add(value); inspector.Capture();
        Assert.Equal("leased", inspector.ReadObject(lease, ["Name"]).Value);
        root.Children.Remove(value); inspector.Capture();
        Assert.Throws<KeyNotFoundException>(() => inspector.InspectObject(lease));
        var stale = inspector.ReadObject(id, ["DataContext"]);
        Assert.Null(stale.ObjectId); Assert.NotNull(stale.ReferenceError);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Asynchronous_results_do_not_resurrect_removed_origins_or_cancelled_calls(bool cancel)
    {
        var model = new PendingResult(); var view = new Border { DataContext = model };
        var panel = new StackPanel { Children = { view } }; using var inspector = new AvaloniaRuntimeInspector(panel);
        var id = inspector.Capture().Nodes.Single(node => node.Type == typeof(Border).FullName).Id;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = inspector.InvokeMethodAsync(id, ["DataContext"], "Wait()", [], inspector.Revision, cancellation.Token);
        Assert.Equal(1, model.Calls); Assert.False(operation.IsCompleted);
        if (cancel) cancellation.Cancel();
        panel.Children.Remove(view); inspector.Capture(); model.Complete();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else
        {
            var result = await operation;
            Assert.Null(result.ObjectId); Assert.NotNull(result.ReferenceError);
        }
        Assert.Empty(inspector.ObjectHandles().Handles); Assert.Equal(1, model.Calls);
    }

    [AvaloniaFact]
    public async Task A_full_lease_table_does_not_fail_or_replay_a_successful_application_method()
    {
        var factory = new PayloadFactory(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = factory });
        var root = inspector.Capture().RootId; string? first = null;
        for (var i = 0; i < 512; i++)
        {
            var result = await inspector.InvokeMethodAsync(root, ["DataContext"], "Next()", [], inspector.Revision, TestContext.Current.CancellationToken);
            Assert.NotNull(result.ObjectId); first ??= result.ObjectId;
        }
        var overflow = await inspector.InvokeMethodAsync(root, ["DataContext"], "Next()", [], inspector.Revision, TestContext.Current.CancellationToken);
        Assert.Null(overflow.ObjectId); Assert.NotNull(overflow.ReferenceError); Assert.Equal(513, factory.Calls);
        Assert.Equal(512, inspector.ObjectHandles().Handles.Count);
        inspector.ReleaseObjectHandles([first!]);
        var next = await inspector.InvokeMethodAsync(root, ["DataContext"], "Next()", [], inspector.Revision, TestContext.Current.CancellationToken);
        Assert.NotNull(next.ObjectId); Assert.NotEqual(first, next.ObjectId); Assert.Equal(514, factory.Calls);
        Assert.Equal(512, inspector.ReleaseObjectHandles()); Assert.Equal(0, factory.Disposals);
    }

    [AvaloniaFact]
    public async Task Releasing_leases_releases_gc_roots_without_disposing_application_objects()
    {
        var factory = new PayloadFactory(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = factory });
        var root = inspector.Capture().RootId;
        var result = await inspector.InvokeMethodAsync(root, ["DataContext"], "Next()", [], inspector.Revision, TestContext.Current.CancellationToken);
        Collect(); Assert.True(IsAlive(factory.Last!));
        inspector.ReleaseObjectHandles([result.ObjectId!]); Collect();
        Assert.False(IsAlive(factory.Last!)); Assert.Equal(0, factory.Disposals);
    }

    [AvaloniaFact]
    public void Typed_dictionary_keys_values_and_read_only_generic_dictionaries_are_supported()
    {
        var mutable = new GenericMap<int, string> { [7] = "seven", [9] = "nine" };
        var view = new Border { DataContext = mutable }; using var inspector = new AvaloniaRuntimeInspector(view);
        var root = inspector.Capture().RootId;
        var page = inspector.DictionaryEntries(root, ["DataContext"], 0, 1);
        Assert.Equal(typeof(int).FullName, page.KeyType); Assert.Equal(typeof(string).FullName, page.ValueType);
        Assert.False(page.ReadOnly); Assert.Equal(2, page.Total); Assert.True(page.HasMore);
        Assert.Equal(7, Assert.Single(page.Entries).Key.Value);
        inspector.SetDictionaryEntry(root, ["DataContext"], Literal(7), Literal("changed"), false, inspector.Revision);
        Assert.Equal("changed", mutable[7]);
        inspector.SetDictionaryEntry(root, ["DataContext"], Literal(9), null, true, inspector.Revision);
        Assert.False(mutable.ContainsKey(9));
        Assert.Throws<ArgumentException>(() => inspector.ReadDictionaryEntry(root, ["DataContext"], Literal("seven"), typeof(string).FullName));
        view.DataContext = new ReadOnlyMap(mutable);
        page = inspector.DictionaryEntries(root, ["DataContext"]);
        Assert.True(page.ReadOnly); Assert.Equal("changed", Assert.Single(page.Entries).Value.Value);
        Assert.Equal("changed", inspector.ReadDictionaryEntry(root, ["DataContext"], Literal(7)).Value);
        Assert.Throws<InvalidOperationException>(() => inspector.SetDictionaryEntry(root, ["DataContext"], Literal(7), Literal("bad"), false, inspector.Revision));
        Assert.Equal("changed", mutable[7]);
    }

    [AvaloniaFact]
    public void Dictionary_reference_keys_and_values_round_trip_without_serializing_object_graphs()
    {
        var key = new object(); var value = new Counter(); var values = new Dictionary<object, object> { [key] = value };
        using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = values });
        var root = inspector.Capture().RootId;
        var entry = Assert.Single(inspector.DictionaryEntries(root, ["DataContext"]).Entries);
        Assert.Null(entry.Key.Value); Assert.Null(entry.Value.Value);
        Assert.NotNull(entry.Key.ObjectId); Assert.NotNull(entry.Value.ObjectId);
        Assert.Equal(entry.Value.ObjectId, inspector.ReadDictionaryEntry(root, ["DataContext"], new(ObjectId: entry.Key.ObjectId)).ObjectId);
        inspector.SetDictionaryEntry(root, ["DataContext"], new(ObjectId: entry.Key.ObjectId), new(ObjectId: entry.Value.ObjectId), false, inspector.Revision);
        Assert.Same(value, values[key]);
        inspector.ReleaseObjectHandles([entry.Key.ObjectId]);
        Assert.Throws<KeyNotFoundException>(() => inspector.SetDictionaryEntry(root, ["DataContext"], new(ObjectId: entry.Key.ObjectId), Literal(null), false, inspector.Revision));
        Assert.Same(value, values[key]);
    }

    [AvaloniaFact]
    public void Nested_value_type_edits_write_back_through_properties_collections_and_nullable_slots()
    {
        var model = new StructModel(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = model });
        var root = inspector.Capture().RootId;
        inspector.SetObjectMember(root, ["DataContext", "Point", "X"], Literal(7), inspector.Revision);
        Assert.Equal(7, model.Point.X);
        inspector.SetObjectMember(root, ["DataContext", "Region", "Point", "X"], Literal(8), inspector.Revision);
        Assert.Equal(8, model.Region.Point.X);
        inspector.SetObjectMember(root, ["DataContext", "Points", "0", "X"], Literal(9), inspector.Revision);
        Assert.Equal(9, model.Points[0].X);
        inspector.SetObjectMember(root, ["DataContext", "Map", "point", "X"], Literal(10), inspector.Revision);
        Assert.Equal(10, model.Map["point"].X);
        inspector.SetObjectMember(root, ["DataContext", "Optional", "X"], Literal(11), inspector.Revision);
        Assert.Equal(11, model.Optional!.Value.X);
        var clamped = inspector.SetObjectMember(root, ["DataContext", "Clamped", "X"], Literal(100), inspector.Revision);
        Assert.Equal(20, model.Clamped.X); Assert.Equal(20, clamped.Value);
        inspector.CreateObjectMember(root, ["DataContext", "Region", "Point"], typeof(MutablePoint).FullName!,
            new Dictionary<string, RuntimeArgument> { ["X"] = Literal(12) }, inspector.Revision);
        Assert.Equal(12, model.Region.Point.X);
    }

    [AvaloniaFact]
    public void Readonly_value_owners_reject_copyback_but_allow_edits_to_referenced_children()
    {
        var model = new StructModel(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = model });
        var root = inspector.Capture().RootId; var revision = inspector.Revision;
        Assert.Throws<InvalidOperationException>(() => inspector.SetObjectMember(root, ["DataContext", "ReadonlyRegion", "Point", "X"], Literal(7), revision));
        Assert.Equal(revision, inspector.Revision); Assert.Equal(0, model.ReadonlyRegion.Point.X);
        inspector.SetObjectMember(root, ["DataContext", "ReadonlyRegion", "Child", "Title"], Literal("changed"), inspector.Revision);
        Assert.Equal("changed", model.ReadonlyRegion.Child.Title);
        inspector.SetObjectMember(root, ["DataContext", "Boxed", "X"], Literal(13), inspector.Revision);
        Assert.Equal(13, ((MutablePoint)model.Boxed).X);
    }

    [AvaloniaFact]
    public void Object_and_collection_watches_publish_live_changes_and_retire_replaced_paths()
    {
        var model = new ObservableModel(); var view = new Border { DataContext = model };
        using var inspector = new AvaloniaRuntimeInspector(view); var root = inspector.Capture().RootId;
        inspector.ReadObject(root, ["DataContext"]); inspector.ReadObject(root, ["DataContext"]);
        inspector.ReadObject(root, ["DataContext", "Items"]);
        Assert.Equal(1, model.Subscribers);
        var sequence = inspector.Changes().Sequence;
        model.Title = "new"; model.Items.Add("one");
        var changes = inspector.Changes(sequence).Changes;
        Assert.Contains(changes, change => change.Kind == "object_property" && change.Name == "DataContext.Title");
        Assert.Contains(changes, change => change.Kind == "object_collection" && change.Name == "DataContext.Items.Add");
        view.DataContext = new ObservableModel(); sequence = inspector.Changes().Sequence;
        model.Title = "old model"; model.Items.Add("ignored");
        Assert.Equal(0, model.Subscribers); Assert.Empty(inspector.Changes(sequence).Changes);
        inspector.ReadObject(root, ["DataContext"]);
        Assert.Equal(1, ((ObservableModel)view.DataContext).Subscribers);
        inspector.ClearObjectWatches(); Assert.Equal(0, ((ObservableModel)view.DataContext).Subscribers);
    }

    [AvaloniaFact]
    public void Background_changes_coalesce_and_queued_notifications_are_cancelled_on_disposal()
    {
        var model = new ObservableModel(); using var inspector = new AvaloniaRuntimeInspector(new Border { DataContext = model });
        var root = inspector.Capture().RootId; inspector.ReadObject(root, ["DataContext"]);
        var sequence = inspector.Changes().Sequence;
        Burst(model); Dispatcher.UIThread.RunJobs();
        var change = Assert.Single(inspector.Changes(sequence).Changes);
        Assert.Equal("object_changes", change.Kind); Assert.Equal("DataContext.*", change.Name);
        var published = 0; inspector.RuntimeChanged += _ => published++;
        Burst(model); inspector.Dispose(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, published); Assert.Equal(0, model.Subscribers);
    }

    [AvaloniaFact]
    public void Observer_admission_is_bounded_and_removing_the_origin_cleans_up_subscriptions()
    {
        var models = Enumerable.Range(0, 140).Select(_ => new ObservableModel()).ToArray();
        var view = new Border { DataContext = models }; var panel = new StackPanel { Children = { view } };
        using var inspector = new AvaloniaRuntimeInspector(panel);
        var root = inspector.Capture().Nodes.Single(node => node.Type == typeof(Border).FullName).Id;
        for (var i = 0; i < models.Length; i++) inspector.ReadObject(root, ["DataContext", i.ToString()]);
        Assert.Equal(128, models.Sum(model => model.Subscribers));
        panel.Children.Clear(); inspector.Capture();
        Assert.Equal(0, models.Sum(model => model.Subscribers));
        Assert.Empty(inspector.ObjectHandles().Handles);
    }

    private static RuntimeArgument Literal(object? value) => new(JsonSerializer.SerializeToElement(value));
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<object> reference) => reference.TryGetTarget(out _);
    private static void Burst(ObservableModel model)
    {
        var worker = new Thread(() => { for (var i = 0; i < 500; i++) model.Title = i.ToString(); });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
    }
    public interface ICounter
    {
        int Value { get; set; }
        ICounter Child { get; }
        ValueTask<int> Add(int amount);
        bool Same(ICounter other);
    }
    private sealed class Counter(bool child = false) : ICounter
    {
        private readonly ICounter? _child = child ? null : new Counter(true);
        int ICounter.Value { get; set; }
        ICounter ICounter.Child => _child ?? this;
        ValueTask<int> ICounter.Add(int amount) => ValueTask.FromResult(((ICounter)this).Value += amount);
        bool ICounter.Same(ICounter other) => ReferenceEquals(this, other);
    }
    public sealed class PayloadFactory
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public WeakReference<object>? Last { get; private set; }
        public object Next() { var value = new Payload(this, ++Calls); Last = new(value); return value; }
        private sealed class Payload(PayloadFactory owner, int number) : IDisposable
        { public int Number { get; } = number; public void Dispose() => owner.Disposals++; }
    }
    public sealed class PendingResult
    {
        private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<object> Wait() { Calls++; return _completion.Task; }
        public void Complete() => _completion.SetResult(new object());
    }
    public struct MutablePoint { public int X { get; set; } }
    public struct MutableRegion { public MutablePoint Point { get; set; } public ObservableModel Child { get; set; } }
    public sealed class StructModel
    {
        private MutablePoint _clamped;
        public MutablePoint Point { get; set; }
        public object Boxed { get; } = new MutablePoint();
        public MutableRegion Region { get; set; } = new() { Child = new() };
        public MutableRegion ReadonlyRegion => Region;
        public MutablePoint[] Points { get; } = [new()];
        public Dictionary<string, MutablePoint> Map { get; } = new() { ["point"] = new() };
        public MutablePoint? Optional { get; set; } = new MutablePoint();
        public MutablePoint Clamped { get => _clamped; set => _clamped = new() { X = Math.Min(value.X, 20) }; }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    public sealed class ObservableModel : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _changed; private string _title = "initial";
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public ObservableCollection<string> Items { get; } = [];
        public string Title { get => _title; set { _title = value; _changed?.Invoke(this, new(nameof(Title))); } }
        public event PropertyChangedEventHandler? PropertyChanged { add => _changed += value; remove => _changed -= value; }
    }
    private sealed class GenericMap<TKey, TValue> : IDictionary<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _items = [];
        public TValue this[TKey key] { get => _items[key]; set => _items[key] = value; }
        public ICollection<TKey> Keys => _items.Keys;
        public ICollection<TValue> Values => _items.Values;
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(TKey key, TValue value) => _items.Add(key, value);
        public bool ContainsKey(TKey key) => _items.ContainsKey(key);
        public bool Remove(TKey key) => _items.Remove(key);
        public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value!);
        public void Add(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Add(item);
        public void Clear() => _items.Clear();
        public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Contains(item);
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int index) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).CopyTo(array, index);
        public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_items).Remove(item);
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ReadOnlyMap(IDictionary<int, string> items) : IReadOnlyDictionary<int, string>
    {
        public string this[int key] => items[key];
        public IEnumerable<int> Keys => items.Keys;
        public IEnumerable<string> Values => items.Values;
        public int Count => items.Count;
        public bool ContainsKey(int key) => items.ContainsKey(key);
        public bool TryGetValue(int key, out string value) => items.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<int, string>> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
