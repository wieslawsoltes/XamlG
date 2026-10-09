using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XamlG.IntelligentUI;

public sealed record UiDataHandle(string Id, long Version, string Sha256, int Bytes, DateTimeOffset ExpiresAt, string? Label);
public sealed record UiDataReference(string Id, long Version, string Pointer = "", int? Offset = null, int? Count = null, string[]? Fields = null);
public sealed record UiDataPage(JsonElement Value, int? Total, bool HasMore, UiDataHandle Source);
public sealed record UiDataBinding(string Name, UiDataReference Reference);
public sealed record UiBindData(string Id, long ExpectedRevision, UiDataBinding[] Bindings, JsonElement? Data = null);

/// <summary>A registered data provider. The principal is supplied by the trusted host; URLs,
/// credentials and executable expressions are not part of a data reference.</summary>
public interface IUiDataResolver
{
    ValueTask<UiDataPage> ResolveAsync(UiDataReference reference, string principal, int maximumBytes, CancellationToken cancellationToken = default);
}

/// <summary>Bounded retained tool results. Handles are version-pinned and owner-scoped rather
/// than bearer capabilities. Expired, released and foreign results are indistinguishable.</summary>
public sealed class UiDataStore : IUiDataResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maximumEntries, _maximumBytes, _maximumEntryBytes;
    private long _version;
    private int _bytes;
    public UiDataStore(int maximumEntries = 64, int maximumBytes = 16 * 1024 * 1024, int maximumEntryBytes = 4 * 1024 * 1024, TimeProvider? timeProvider = null)
    {
        if (maximumEntries is < 1 or > 1024 || maximumBytes is < 128 or > 128 * 1024 * 1024 || maximumEntryBytes < 2 || maximumEntryBytes > maximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes; _maximumEntryBytes = maximumEntryBytes; _time = timeProvider ?? TimeProvider.System;
    }
    public UiDataHandle Put(JsonElement value, string principal, string? label = null, TimeSpan? lifetime = null)
    {
        Principal(principal);
        if (label?.Length > 200) throw new UiException("invalid_data", "Data labels are limited to 200 characters.");
        var duration = lifetime ?? TimeSpan.FromMinutes(30);
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(24)) throw new UiException("invalid_data", "Data lifetime must be between one second and 24 hours.");
        var bytes = ValidateJson(value, _maximumEntryBytes); var owned = value.Clone();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
        lock (_gate)
        {
            Expire();
            if (_entries.Count >= _maximumEntries || bytes > _maximumBytes - _bytes) throw new UiException("data_limit", "Release retained data before adding another tool result.");
            var handle = new UiDataHandle(Guid.NewGuid().ToString("N"), checked(++_version), hash, bytes, _time.GetUtcNow() + duration, label);
            _entries.Add(handle.Id, new(principal, handle, owned)); _bytes += bytes; return handle;
        }
    }
    public IReadOnlyList<UiDataHandle> List(string principal)
    {
        Principal(principal); lock (_gate) { Expire(); return _entries.Values.Where(entry => entry.Principal == principal).OrderBy(entry => entry.Handle.Version).Select(entry => entry.Handle).ToArray(); }
    }
    public ValueTask<UiDataPage> ResolveAsync(UiDataReference reference, string principal, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference); Principal(principal); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes is < 2 or > 4 * 1024 * 1024 || reference.Pointer == null || reference.Pointer.Length > 2048 || reference.Offset is < 0 || reference.Count is < 0 or > 4096 || reference.Offset.HasValue != reference.Count.HasValue || reference.Fields?.Length > 128)
            throw new UiException("invalid_reference", "Invalid JSON pointer, page or projection limits.");
        Entry entry;
        lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(reference.Id, out entry!) || entry.Principal != principal || entry.Handle.Version != reference.Version) throw new UiException("unknown_data", "The referenced data is no longer available.");
        }
        var value = UiJsonPointer.Resolve(entry.Value, reference.Pointer); int? total = null; var more = false;
        if (reference.Offset is { } offset)
        {
            if (value.ValueKind != JsonValueKind.Array) throw new UiException("invalid_reference", "Paging requires an array.");
            total = value.GetArrayLength();
            if (offset > total.Value) throw new UiException("invalid_reference", "Page offset exceeds the array length.");
            var count = Math.Min(reference.Count!.Value, total.Value - offset); more = offset + count < total.Value;
            value = JsonSerializer.SerializeToElement(value.EnumerateArray().Skip(offset).Take(count).ToArray());
        }
        if (reference.Fields is { } fields)
        {
            if (fields.Length == 0 || fields.Any(field => field == null || field.Length > 256) || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) throw new UiException("invalid_reference", "Use distinct nonempty projection fields.");
            JsonElement Project(JsonElement item)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object) throw new UiException("invalid_reference", "Projection requires objects.");
                var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var field in fields) if (item.TryGetProperty(field, out var found)) result.Add(field, found); else throw new UiException("invalid_reference", "A projected field is missing: " + field);
                return JsonSerializer.SerializeToElement(result);
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() > 4096) throw new UiException("invalid_reference", "Page the array before projecting more than 4096 rows.");
                value = JsonSerializer.SerializeToElement(value.EnumerateArray().Select(Project).ToArray());
            }
            else value = Project(value);
        }
        ValidateJson(value, maximumBytes); cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new UiDataPage(value.Clone(), total, more, entry.Handle));
    }
    public bool Release(UiDataReference reference, string principal)
    {
        Principal(principal); lock (_gate)
        {
            Expire();
            if (!_entries.TryGetValue(reference.Id, out var entry) || entry.Principal != principal || entry.Handle.Version != reference.Version) return false;
            _entries.Remove(reference.Id); _bytes -= entry.Handle.Bytes; return true;
        }
    }
    public void ReleasePrincipal(string principal)
    {
        Principal(principal); lock (_gate) foreach (var entry in _entries.Values.Where(entry => entry.Principal == principal).ToArray()) { _entries.Remove(entry.Handle.Id); _bytes -= entry.Handle.Bytes; }
    }
    public void Clear() { lock (_gate) { _entries.Clear(); _bytes = 0; } }
    private void Expire()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _entries.Values.Where(entry => entry.Handle.ExpiresAt <= now).ToArray()) { _entries.Remove(entry.Handle.Id); _bytes -= entry.Handle.Bytes; }
    }
    internal static int ValidateJson(JsonElement value, int maximumBytes)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw new UiException("invalid_data", "Undefined JSON data.");
        var length = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (length > maximumBytes) throw new UiException("data_limit", "Resolved data exceeds the byte limit; select fewer fields or rows.");
        Check(value, 0); return length;
        static void Check(JsonElement item, int depth)
        {
            if (depth > 32) throw new UiException("data_limit", "JSON exceeds 32 levels.");
            if (item.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject()) { if (!names.Add(property.Name)) throw new UiException("invalid_data", "Duplicate JSON member."); Check(property.Value, depth + 1); }
            }
            else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Check(child, depth + 1);
        }
    }
    private static void Principal(string value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 200) throw new UiException("invalid_principal", "Use a transport-derived principal."); }
    private sealed record Entry(string Principal, UiDataHandle Handle, JsonElement Value);
}

/// <summary>RFC 6901 string syntax, including escaped slash/tilde and strict array indices.</summary>
public static class UiJsonPointer
{
    public static JsonElement Resolve(JsonElement root, string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length > 2048 || pointer.Length > 0 && pointer[0] != '/') throw new UiException("invalid_pointer", "Use an empty pointer or '/'-prefixed RFC 6901 pointer.");
        var value = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var key = new StringBuilder();
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '~') { key.Append(raw[i]); continue; }
                if (++i == raw.Length || raw[i] is not ('0' or '1')) throw new UiException("invalid_pointer", "Invalid JSON pointer escape.");
                key.Append(raw[i] == '0' ? '~' : '/');
            }
            var name = key.ToString();
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var next)) value = next;
            else if (value.ValueKind == JsonValueKind.Array && name.Length > 0 && (name == "0" || name[0] is >= '1' and <= '9') && name.All(char.IsAsciiDigit) && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength()) value = value[index];
            else throw new UiException("invalid_pointer", "JSON pointer does not identify a value.");
        }
        return value;
    }
}

public sealed partial class UiSessionStore
{
    public async ValueTask<UiSnapshot> BindDataAsync(UiBindData request, string principal, IUiDataResolver resolver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(resolver); Principal(principal);
        if (request.Bindings == null || request.Bindings.Length is < 1 or > 32 || request.Bindings.Any(binding => binding == null || binding.Reference == null) || request.Bindings.Select(binding => binding.Name).Distinct(StringComparer.Ordinal).Count() != request.Bindings.Length) throw new UiException("invalid_reference", "Use 1–32 distinct data bindings.");
        UiSnapshot original; long lifetime;
        lock (_gate)
        {
            original = Get(request.Id, principal).Snapshot; lifetime = _lifetime;
            if (original.Revision != request.ExpectedRevision) throw new UiException("revision_conflict", "The target UI changed before resolving data.");
        }
        var data = UiJson.Object(request.Data ?? original.Data, Compiler.Limits.DataBytes, "Data").EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        foreach (var binding in request.Bindings)
        {
            UiJson.Identifier(binding.Name, "Data binding name");
            var page = await resolver.ResolveAsync(binding.Reference, principal, Compiler.Limits.DataBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); data[binding.Name] = page.Value.Clone();
        }
        var resolved = UiJson.Object(JsonSerializer.SerializeToElement(data), Compiler.Limits.DataBytes, "Resolved data"); UiSnapshot snapshot;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lifetime != lifetime) throw new UiException("workspace_changed", "The UI workspace was retired while data was resolving.");
            var entry = Get(request.Id, principal);
            if (entry.Snapshot.Revision != request.ExpectedRevision || entry.Snapshot.SessionId != original.SessionId) throw new UiException("revision_conflict", "The UI changed while data was resolving.");
            var roots = entry.Template.Render(entry.Snapshot.State, resolved); ValidateActionReferences(roots, entry.Snapshot.Actions);
            snapshot = entry.Snapshot with { Revision = checked(++_revision), Data = resolved, Roots = roots, FallbackMarkdown = Fallback(roots, entry.Markdown) };
            _entries[request.Id] = entry with { Snapshot = snapshot }; _generation = checked(_generation + 1);
        }
        Notify(snapshot); return snapshot;
    }
}
