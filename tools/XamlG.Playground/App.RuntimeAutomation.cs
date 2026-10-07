using XamlG.Automation;
using XamlG.AvaloniaRuntime.Inspection;

namespace XamlG.Playground;

public partial class App
{
    private void AddRuntimeObjectAutomation()
    {
        AddRuntimeInputAutomation();
        AddAutomation<NoArguments>("runtime_object_handles", "List retained result-object handles, their originating tree/peer handles and absolute expiry. Up to 512 objects are retained for five minutes; preview replacement and origin removal retire them.", AutomationScope.Runtime, AutomationEffect.Read,
            (_, _) => RuntimeInspector().ObjectHandles());
        AddAutomation<RuntimeReleaseHandlesArguments>("runtime_object_handles_release", "Release selected retained object handles, or omit objectIds to release all. Releases inspector references and watches without disposing application objects. Tree and peer handles cannot be released here.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); return new { runtime.Revision, released = runtime.ReleaseObjectHandles(args.ObjectIds) }; });
        AddAutomation<RuntimeDictionaryArguments>("runtime_dictionary_entries", "Read a bounded page of dictionary keys and values, including numeric, enum, object and other non-string keys. Read-only generic dictionaries are supported.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().DictionaryEntries(args.ObjectId, args.Path, args.Offset, args.Count));
        AddAutomation<RuntimeDictionaryReadArguments>("runtime_dictionary_read", "Read a typed dictionary key. Supply keyType for a heterogeneous object-keyed dictionary, or a live object reference for an identity key.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var value = runtime.ReadDictionaryEntry(args.ObjectId, args.Path, args.Key, args.KeyType); return new { runtime.Revision, value }; });
        AddAutomation<RuntimeDictionarySetArguments>("runtime_dictionary_set", "Set or remove a typed live dictionary entry after checking its runtime revision. Observed INPC and collection changes enter the bounded runtime journal.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var value = runtime.SetDictionaryEntry(args.ObjectId, args.Path, args.Key, args.Value, args.Remove, args.ExpectedRevision, args.KeyType); return new { runtime.Revision, value }; });
        AddAutomation<NoArguments>("runtime_object_watches_clear", "Release the bounded view-model/property/collection change observers acquired by object and dictionary inspection.", AutomationScope.Runtime, AutomationEffect.Read,
            (_, _) => { RuntimeInspector().ClearObjectWatches(); return new { watching = false }; });
        AddAutomation<RuntimeSourceArguments>("runtime_source", "Read exact Avalonia source metadata and generated-object provenance for a live object path. Optional resourceKey reads keyed metadata without constructing a deferred resource.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().Source(args.ObjectId, args.Path, args.ResourceKey));
        AddAutomation<RuntimeTypesArguments>("runtime_types", "Discover loaded runtime types, assemblies and public construction support with bounded pagination.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => new { types = RuntimeInspector().Types(args.Query, args.Offset, args.Count) });
        AddAutomation<RuntimeInspectArguments>("runtime_object_inspect", "Inspect a tree, accessibility-peer or retained result-object ID and optional member path. Lists bounded members, exact methods and public interfaces. Set interfaceName to inspect explicit implementations, including returned text-range providers. Getters execute application code.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => RuntimeInspector().InspectObject(args.ObjectId, args.Path, args.Offset, args.Count, args.IncludeNonPublic, args.InterfaceName));
        AddAutomation<RuntimePathArguments>("runtime_object_read", "Read a property, dictionary-key or collection-index path from a tree, peer or retained object handle. Optional interfaceName selects the final member's public interface. Returned object IDs can be inspected or used as typed arguments.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var value = runtime.ReadObject(args.ObjectId, args.Path, args.InterfaceName); return new { runtime.Revision, value }; });
        AddAutomation<RuntimeMemberArguments>("runtime_object_set", "Set a public member or collection entry at a live object path. Accepts literals or typed tree, peer and retained-object references; interfaceName supports explicit public-interface setters.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var value = runtime.SetObjectMember(args.ObjectId, args.Path, args.Argument, args.ExpectedRevision, args.InterfaceName); return new { runtime.Revision, value }; });
        AddAutomation<RuntimeCreateMemberArguments>("runtime_object_create", "Construct a concrete loaded type with a public parameterless constructor and assign it to a writable live member, including DataContext. Executes constructors and setters.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var value = runtime.CreateObjectMember(args.ObjectId, args.Path, args.Type, args.InitialValues, args.ExpectedRevision, args.InterfaceName); return new { runtime.Revision, value }; });
        _automation.Add<RuntimeInvokeArguments, object>("xamlg_runtime_method_invoke", "Invoke an exact public method on a tree, accessibility peer or retained result object. Optional interfaceName selects explicit public-interface methods. Awaits Task/ValueTask results and retains non-scalar results with bounded leases; cancellation does not roll back application effects.", AutomationScope.Runtime, AutomationEffect.Execute,
            async (args, context) =>
            {
                var runtime = RuntimeInspector(); var result = await runtime.InvokeMethodAsync(args.ObjectId, args.Path, args.Signature, args.Arguments, args.ExpectedRevision, context.CancellationToken, args.InterfaceName);
                var snapshot = runtime.Capture(); return new { snapshot.Revision, result };
            });
        AddAutomation<RuntimeCreateChildArguments>("runtime_child_create", "Construct and insert a live Control into a Panel, empty content/decorator, or unbound ItemsControl. Does not modify XAML source.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => RuntimeInspector().CreateChild(args.ParentId, args.Type, args.InitialValues, args.Index, args.ExpectedRevision));
        AddAutomation<RuntimeMutationArguments>("runtime_child_remove", "Remove a live child from an editable framework collection. Root and template-owned visuals cannot be removed through this operation.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => RuntimeInspector().RemoveChild(args.ObjectId, args.ExpectedRevision));
        AddAutomation<RuntimeReparentArguments>("runtime_child_reparent", "Move a live control between editable containers, or reorder it within its parent. Rejects cycles and stale revisions.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => RuntimeInspector().Reparent(args.ObjectId, args.ParentId, args.Index, args.ExpectedRevision));
        AddAutomation<RuntimeWatchArguments>("runtime_event_watch", "Record a routed event in the bounded runtime journal, including already handled events. Watches retire when their object detaches.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { RuntimeInspector().WatchEvent(args.ObjectId, args.Event); return new { watching = true }; });
        AddAutomation<NoArguments>("runtime_event_watches_clear", "Release all event watches owned by the current runtime inspection session.", AutomationScope.Runtime, AutomationEffect.Read,
            (_, _) => { RuntimeInspector().ClearEventWatches(); return new { watching = false }; });
        AddAutomation<RuntimeObjectArguments>("runtime_value_frames", "Inspect Avalonia's actual applied value frames, including active selectors, priorities and property values. Diagnostic metadata depends on the loaded Avalonia version.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var frames = runtime.ValueFrames(args.ObjectId); return new { runtime.Revision, frames }; });
        AddAutomation<RuntimeObjectArguments>("runtime_bindings", "Inspect active binding expressions, their descriptions, state, validation/error category, priority and effective values.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var bindings = runtime.Bindings(args.ObjectId); return new { runtime.Revision, bindings }; });
        AddAutomation<RuntimeBindArguments>("runtime_binding_set", "Install a live Avalonia binding with an explicit mode and optional live source reference; default source is DataContext. Disposed with the inspection session.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var binding = runtime.SetBinding(args.ObjectId, args.Property, args.Path, args.Mode, args.Source, args.ExpectedRevision); return new { runtime.Revision, binding }; });
        AddAutomation<RuntimeBindingUpdateArguments>("runtime_binding_update", "Explicitly update an active binding's source or target through Avalonia's binding engine.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.UpdateBinding(args.ObjectId, args.Property, args.UpdateSource, args.ExpectedRevision); return new { runtime.Revision }; });
        AddAutomation<RuntimeObjectArguments>("runtime_styles", "List local styles, selectors and setter values on a live styled element.", AutomationScope.Runtime, AutomationEffect.Read,
            (args, _) => { var runtime = RuntimeInspector(); var styles = runtime.Styles(args.ObjectId); return new { runtime.Revision, styles }; });
        AddAutomation<RuntimeStyleAddArguments>("runtime_style_add", "Append a live style scoped to a styled element, matching a type and optional class, with typed property setters.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); var index = runtime.AddStyle(args.ObjectId, args.TargetType, args.ClassName, args.Setters, args.ExpectedRevision); return new { runtime.Revision, index }; });
        AddAutomation<RuntimeStyleRemoveArguments>("runtime_style_remove", "Remove an indexed local style after checking the current runtime revision.", AutomationScope.Runtime, AutomationEffect.Execute,
            (args, _) => { var runtime = RuntimeInspector(); runtime.RemoveStyle(args.ObjectId, args.Index, args.ExpectedRevision); return new { runtime.Revision }; });
    }

    public sealed record RuntimeTypesArguments(string Query = "", int Offset = 0, int Count = 100);
    public sealed record RuntimeReleaseHandlesArguments(string[]? ObjectIds = null);
    public sealed record RuntimeDictionaryArguments(string ObjectId, string[]? Path = null, int Offset = 0, int Count = 100);
    public sealed record RuntimeDictionaryReadArguments(string ObjectId, RuntimeArgument Key, string[]? Path = null, string? KeyType = null);
    public sealed record RuntimeDictionarySetArguments(string ObjectId, RuntimeArgument Key, long ExpectedRevision, RuntimeArgument? Value = null, bool Remove = false, string[]? Path = null, string? KeyType = null);
    public sealed record RuntimeSourceArguments(string ObjectId, string[]? Path = null, RuntimeArgument? ResourceKey = null);
    public sealed record RuntimeInspectArguments(string ObjectId, string[]? Path = null, int Offset = 0, int Count = 100, bool IncludeNonPublic = false, string? InterfaceName = null);
    public sealed record RuntimePathArguments(string ObjectId, string[] Path, string? InterfaceName = null);
    public sealed record RuntimeMemberArguments(string ObjectId, string[] Path, RuntimeArgument Argument, long ExpectedRevision, string? InterfaceName = null);
    public sealed record RuntimeCreateMemberArguments(string ObjectId, string[] Path, string Type, long ExpectedRevision, Dictionary<string, RuntimeArgument>? InitialValues = null, string? InterfaceName = null);
    public sealed record RuntimeInvokeArguments(string ObjectId, string Signature, RuntimeArgument[] Arguments, long ExpectedRevision, string[]? Path = null, string? InterfaceName = null);
    public sealed record RuntimeCreateChildArguments(string ParentId, string Type, long ExpectedRevision, int Index = -1, Dictionary<string, RuntimeArgument>? InitialValues = null);
    public sealed record RuntimeReparentArguments(string ObjectId, string ParentId, long ExpectedRevision, int Index = -1);
    public sealed record RuntimeWatchArguments(string ObjectId, string Event);
    public sealed record RuntimeBindArguments(string ObjectId, string Property, string Path, long ExpectedRevision, string Mode = "Default", RuntimeArgument? Source = null);
    public sealed record RuntimeBindingUpdateArguments(string ObjectId, string Property, long ExpectedRevision, bool UpdateSource = false);
    public sealed record RuntimeStyleAddArguments(string ObjectId, string TargetType, Dictionary<string, RuntimeArgument> Setters, long ExpectedRevision, string? ClassName = null);
    public sealed record RuntimeStyleRemoveArguments(string ObjectId, int Index, long ExpectedRevision);
}
