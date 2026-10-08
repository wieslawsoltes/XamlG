using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

internal static class AllocationStacks
{
    public static object Read(string tracePath)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "xamlg-allocation-stacks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var converted = TraceLog.CreateFromEventPipeDataFile(tracePath, Path.Combine(temporary, "compiler.etlx"));
            using var trace = new TraceLog(converted);
            var frames = new Dictionary<string, long>(StringComparer.Ordinal);
            var owners = new Dictionary<string, long>(StringComparer.Ordinal);
            var nearestFrames = new Dictionary<string, long>(StringComparer.Ordinal);
            var nearestGeneratorFrames = new Dictionary<string, long>(StringComparer.Ordinal);
            long withStack = 0, withoutStack = 0;
            foreach (var data in trace.Events)
            {
                if (data is not GCAllocationTickTraceData allocation) continue;
                var stack = data.CallStack();
                if (stack == null) { withoutStack += allocation.AllocationAmount64; continue; }
                withStack += allocation.AllocationAmount64;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? owner = null;
                string? generator = null;
                for (; stack != null; stack = stack.Caller)
                {
                    var address = stack.CodeAddress;
                    var module = address.ModuleName;
                    var name = address.FullMethodName;
                    if (string.IsNullOrEmpty(name)) continue;
                    var key = module + "!" + name;
                    if (seen.Add(key)) Add(frames, key, allocation.AllocationAmount64);
                    // Attribute each interval to the nearest generator, analyzer or
                    // Roslyn frame. Inclusive frames below deliberately overlap.
                    if (generator == null && module.StartsWith("XamlG.", StringComparison.Ordinal)) generator = key;
                    if (owner == null && (module.StartsWith("XamlG.", StringComparison.Ordinal) ||
                        module.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) ||
                        module.Contains("Analyzer", StringComparison.Ordinal)))
                    {
                        owner = module;
                        Add(nearestFrames, key, allocation.AllocationAmount64);
                    }
                }
                Add(owners, owner ?? "other/unresolved", allocation.AllocationAmount64);
                if (generator != null) Add(nearestGeneratorFrames, generator, allocation.AllocationAmount64);
            }
            return new
            {
                scope = "Sampled allocation intervals attributed using allocation-event call stacks; " +
                    "not exact per-method allocations. Inclusive rows overlap. Owners are the nearest " +
                    "generator, analyzer or Roslyn frame, not complete phase totals.",
                bytesWithStack = withStack,
                bytesWithoutStack = withoutStack,
                nearestOwners = Rows(owners),
                nearestFrames = Rows(nearestFrames).Take(60),
                nearestGeneratorFrames = Rows(nearestGeneratorFrames).Take(60),
                inclusive = Rows(frames).Take(60),
                xamlgInclusive = Rows(frames.Where(pair => pair.Key.StartsWith("XamlG.", StringComparison.Ordinal))).Take(60)
            };
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static void Add(Dictionary<string, long> values, string key, long bytes)
    {
        values.TryGetValue(key, out var current);
        values[key] = current + bytes;
    }

    private static IEnumerable<object> Rows(IEnumerable<KeyValuePair<string, long>> values) =>
        values.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (object)new { frame = pair.Key, estimatedBytes = pair.Value });
}
