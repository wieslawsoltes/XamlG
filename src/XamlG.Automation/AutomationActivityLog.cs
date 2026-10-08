namespace XamlG.Automation;

public sealed record AutomationActivity(long Sequence, DateTimeOffset Time, string Method, string Name, string Caller, string Status, double ElapsedMilliseconds, string? ErrorCode);

/// <summary>Bounded metadata-only audit. Arguments, results and credentials are never retained.</summary>
public sealed class AutomationActivityLog
{
    private readonly object _gate = new();
    private readonly Queue<AutomationActivity> _items = new();
    private long _sequence;
    private int _characters;
    public IReadOnlyList<AutomationActivity> Snapshot { get { lock (_gate) return _items.ToArray(); } }
    public void Add(string method, string name, string caller, string status, double elapsedMilliseconds, string? errorCode = null)
    {
        lock (_gate)
        {
            var item = new AutomationActivity(++_sequence, DateTimeOffset.UtcNow, Clip(method, 40), Clip(name, 256), Clip(caller, 200), Clip(status, 32), elapsedMilliseconds, errorCode == null ? null : Clip(errorCode, 128));
            _items.Enqueue(item); _characters += Size(item);
            while (_items.Count > 500 || _characters > 512_000) _characters -= Size(_items.Dequeue());
        }
    }
    public void Clear() { lock (_gate) { _items.Clear(); _characters = 0; } }
    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + " [truncated]";
    private static int Size(AutomationActivity item) => item.Method.Length + item.Name.Length + item.Caller.Length + item.Status.Length + (item.ErrorCode?.Length ?? 0) + 128;
}
