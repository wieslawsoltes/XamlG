using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Avalonia.Threading;

namespace XamlG.AvaloniaRuntime.Inspection;

public sealed partial class AvaloniaRuntimeInspector
{
    private readonly Dictionary<(string Root, string Path), ObjectWatch> _objectWatches = new();

    public RuntimeDictionarySnapshot DictionaryEntries(string objectId, IReadOnlyList<string>? path = null, int offset = 0, int count = 100)
    {
        if (offset is < 0 or > 100000 || count is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(offset));
        var target = FollowPath(ResolveObjectTarget(objectId), path) ?? throw new InvalidOperationException("The dictionary target is null.");
        var dictionary = DictionaryAccess.Create(target); ObserveObject(objectId, path, target);
        var entries = dictionary.Keys().Cast<object>().Skip(offset).Take(count).Select(key =>
            new RuntimeDictionaryEntry(DescribeObjectValue(key, objectId), DescribeObjectValue(dictionary.Get(key), objectId), TypeName(key.GetType()))).ToArray();
        return new(Revision, TypeName(dictionary.KeyType), TypeName(dictionary.ValueType), dictionary.ReadOnly, dictionary.Count(), offset,
            offset + entries.Length < dictionary.Count(), entries);
    }
    public RuntimeValue ReadDictionaryEntry(string objectId, IReadOnlyList<string>? path, RuntimeArgument key, string? keyType = null)
    {
        var target = FollowPath(ResolveObjectTarget(objectId), path) ?? throw new InvalidOperationException("The dictionary target is null.");
        var dictionary = DictionaryAccess.Create(target); var converted = DictionaryKey(dictionary, key, keyType);
        if (!dictionary.Contains(converted)) throw new KeyNotFoundException("The dictionary does not contain that key.");
        ObserveObject(objectId, path, target); return DescribeObjectValue(dictionary.Get(converted), objectId);
    }
    public RuntimeValue SetDictionaryEntry(string objectId, IReadOnlyList<string>? path, RuntimeArgument key, RuntimeArgument? value,
        bool remove, long expectedRevision, string? keyType = null)
    {
        var target = FollowPath(ResolveObjectTarget(objectId, expectedRevision), path) ?? throw new InvalidOperationException("The dictionary target is null.");
        var dictionary = DictionaryAccess.Create(target);
        if (dictionary.ReadOnly) throw new InvalidOperationException("The dictionary is read-only.");
        var convertedKey = DictionaryKey(dictionary, key, keyType);
        var convertedValue = remove ? null : ConvertArgument(value ?? throw new ArgumentException("Supply a value when setting a dictionary entry."), dictionary.ValueType);
        if (!ReferenceEquals(FollowPath(ResolveObjectTarget(objectId, expectedRevision), path), target)) throw new InvalidOperationException("The dictionary path changed while preparing the edit.");
        if (Revision != expectedRevision) throw new InvalidOperationException("The runtime changed while resolving the dictionary path.");
        ObserveObject(objectId, path, target);
        if (remove)
        { if (!dictionary.Contains(convertedKey)) throw new KeyNotFoundException("The dictionary does not contain that key."); dictionary.Remove!(convertedKey); }
        else dictionary.Set!(convertedKey, convertedValue);
        Changed(objectId, "dictionary", remove ? "remove" : "set", null);
        return DescribeObjectValue(remove ? null : dictionary.Get(convertedKey), objectId);
    }
    private object DictionaryKey(DictionaryAccess dictionary, RuntimeArgument key, string? keyType)
    {
        var type = keyType == null ? dictionary.KeyType : ResolveType(keyType);
        if (!dictionary.KeyType.IsAssignableFrom(type)) throw new ArgumentException("The key type is not assignable to the dictionary's key type.");
        return ConvertArgument(key, type) ?? throw new ArgumentException("Dictionary keys cannot be null.");
    }

    /// <summary>Disposes bounded INPC/collection observers acquired by live object reads.</summary>
    public void ClearObjectWatches()
    {
        VerifyAccess();
        foreach (var watch in _objectWatches.Values) watch.Cleanup();
        _objectWatches.Clear();
    }
    private void ObserveObject(string root, IReadOnlyList<string>? path, object? target)
    {
        var observedPath = path?.ToArray() ?? [];
        var key = (root, System.Text.Json.JsonSerializer.Serialize(observedPath));
        if (_objectWatches.TryGetValue(key, out var previous))
        {
            if (previous.Target.TryGetTarget(out var observed) && ReferenceEquals(observed, target)) return;
            previous.Cleanup(); _objectWatches.Remove(key);
        }
        if (target is not INotifyPropertyChanged && target is not INotifyCollectionChanged) return;
        if (_objectWatches.Count >= 128)
        { var oldest = _objectWatches.First(); oldest.Value.Cleanup(); _objectWatches.Remove(oldest.Key); }
        var watch = new ObjectWatch(target!); var label = string.Join('.', observedPath);
        void Notify(string kind, string name)
        {
            void Publish(string eventKind, string eventName)
            {
                if (_disposed || !_objectWatches.TryGetValue(key, out var current) || !ReferenceEquals(current, watch)) return;
                if (watch.Publishing) return;
                watch.Publishing = true;
                try
                {
                    // A path may now point at another view model or collection. Old
                    // publishers must not update that new object's revision/history.
                    if (!watch.Target.TryGetTarget(out var observed) || !TryKnownObjectTarget(root, out var owner) ||
                        !ReferenceEquals(FollowPath(owner, observedPath), observed))
                    { watch.Cleanup(); _objectWatches.Remove(key); return; }
                    var description = label.Length == 0 ? eventName : label + "." + eventName;
                    Changed(root, eventKind, description.Length > 1024 ? description[..1024] : description, null);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { watch.Cleanup(); _objectWatches.Remove(key); }
                finally { watch.Publishing = false; }
            }
            if (Dispatcher.UIThread.CheckAccess()) Publish(kind, name);
            else if (Interlocked.Exchange(ref watch.Pending, 1) == 0)
                Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref watch.Pending, 0); Publish("object_changes", "*"); });
        }
        if (target is INotifyPropertyChanged properties)
        {
            PropertyChangedEventHandler handler = (_, args) => Notify("object_property", args.PropertyName ?? "*");
            var publisher = new WeakReference<INotifyPropertyChanged>(properties);
            properties.PropertyChanged += handler; watch.Cleanup += () => { if (publisher.TryGetTarget(out var current)) current.PropertyChanged -= handler; };
        }
        if (target is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, args) => Notify("object_collection", args.Action.ToString());
            var publisher = new WeakReference<INotifyCollectionChanged>(collection);
            collection.CollectionChanged += handler; watch.Cleanup += () => { if (publisher.TryGetTarget(out var current)) current.CollectionChanged -= handler; };
        }
        _objectWatches.Add(key, watch);
    }
    private void RetireObjectWatches(string root)
    {
        foreach (var key in _objectWatches.Keys.Where(key => key.Root == root).ToArray())
        { _objectWatches[key].Cleanup(); _objectWatches.Remove(key); }
    }
    private sealed class ObjectWatch(object target)
    { public WeakReference<object> Target { get; } = new(target); public Action Cleanup = () => { }; public int Pending; public bool Publishing; }

    private sealed record DictionaryAccess(Type KeyType, Type ValueType, bool ReadOnly, Func<int> Count, Func<IEnumerable> Keys,
        Func<object, bool> Contains, Func<object, object?> Get, Action<object, object?>? Set, Action<object>? Remove)
    {
        public static DictionaryAccess Create(object target)
        {
            var interfaces = target.GetType().GetInterfaces();
            var mutable = interfaces.FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            var generic = mutable ?? interfaces.FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
            if (target is IDictionary dictionary)
                return new(generic?.GenericTypeArguments[0] ?? typeof(object), generic?.GenericTypeArguments[1] ?? typeof(object), dictionary.IsReadOnly,
                    () => dictionary.Count, () => dictionary.Keys, dictionary.Contains, key => dictionary[key],
                    (key, value) => { if (dictionary.IsFixedSize && !dictionary.Contains(key)) throw new InvalidOperationException("The dictionary has a fixed size."); dictionary[key] = value; },
                    key => { if (dictionary.IsFixedSize) throw new InvalidOperationException("The dictionary has a fixed size."); dictionary.Remove(key); });
            if (generic == null) throw new ArgumentException("The object is not a dictionary.");
            var properties = generic.GetInterfaces().Prepend(generic).SelectMany(type => type.GetProperties()).ToArray();
            var item = properties.First(property => property.Name == "Item");
            var keys = properties.First(property => property.Name == "Keys"); var count = properties.First(property => property.Name == "Count");
            var readOnly = mutable == null || properties.FirstOrDefault(property => property.Name == "IsReadOnly")?.GetValue(target) is true;
            var contains = generic.GetMethod("ContainsKey")!; var remove = mutable?.GetMethod("Remove", [generic.GenericTypeArguments[0]]);
            return new(generic.GenericTypeArguments[0], generic.GenericTypeArguments[1], readOnly, () => (int)count.GetValue(target)!, () => (IEnumerable)keys.GetValue(target)!,
                key => (bool)contains.Invoke(target, [key])!, key => item.GetValue(target, [key]),
                readOnly ? null : (key, value) => item.SetValue(target, value, [key]), readOnly ? null : key => remove!.Invoke(target, [key]));
        }
    }
}
