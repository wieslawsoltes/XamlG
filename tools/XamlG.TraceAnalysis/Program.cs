using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

if (args.Length != 1)
    throw new ArgumentException("Usage: XamlG.TraceAnalysis <compiler.nettrace>");

using var source = new EventPipeEventSource(args[0]);
var allocations = new Dictionary<string, (long Bytes, long Ticks)>(StringComparer.Ordinal);
var collections = new Dictionary<int, int>();
var pauses = new Dictionary<string, List<double>>(StringComparer.Ordinal);
(string Reason, double Start)? suspended = null;
source.Clr.GCAllocationTick += data =>
{
    var name = data.TypeName ?? "<unknown>";
    allocations.TryGetValue(name, out var value);
    allocations[name] = (value.Bytes + data.AllocationAmount64, value.Ticks + 1);
};
source.Clr.GCStart += data =>
{
    collections.TryGetValue(data.Depth, out var count);
    collections[data.Depth] = count + 1;
};
source.Clr.GCSuspendEEStart += data => suspended = (data.Reason.ToString(), data.TimeStampRelativeMSec);
source.Clr.GCRestartEEStop += data =>
{
    if (suspended is { } suspension)
    {
        if (!pauses.TryGetValue(suspension.Reason, out var values)) pauses[suspension.Reason] = values = new();
        values.Add(data.TimeStampRelativeMSec - suspension.Start);
    }
    suspended = null;
};
source.Process();
Console.WriteLine(JsonSerializer.Serialize(new
{
    trace = Path.GetFullPath(args[0]),
    scope = "GC allocation ticks are sampling estimates, attributed to the type that triggered each tick; " +
            "they are not exact per-type allocation counts. Runtime pauses include suspension and restart; " +
            "reasons are separated because sampling itself can suspend the runtime.",
    eventsLost = source.EventsLost,
    allocatedBytesEstimate = allocations.Values.Sum(value => value.Bytes),
    allocationTicks = allocations.Values.Sum(value => value.Ticks),
    collectionsByGeneration = collections,
    pausesByReason = pauses.Select(pair => new { reason = pair.Key, count = pair.Value.Count,
        totalMilliseconds = pair.Value.Sum(), maximumMilliseconds = pair.Value.Max() }),
    allocations = allocations.OrderByDescending(pair => pair.Value.Bytes).Take(60)
        .Select(pair => new { type = pair.Key, estimatedBytes = pair.Value.Bytes, ticks = pair.Value.Ticks })
}, new JsonSerializerOptions { WriteIndented = true }));
