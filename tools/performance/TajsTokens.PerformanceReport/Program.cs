using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using TajsTokens.Core.Services;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Pass one local .nettrace file. Output contains aggregate TajsTokens work diagnostics only.");
    return 2;
}

using var source = new EventPipeEventSource(args[0]);
var completed = new List<Completion>();
var started = new HashSet<(int Process, long Operation)>();
var invalidations = new Dictionary<(int Process, int Phase, int Reason), int>();
int sourceErrors = 0;
source.Dynamic.All += data =>
{
    if (data.ProviderName != "TajsTokens-Work") return;
    long Number(string name) => Convert.ToInt64(data.PayloadByName(name));
    // Self-describing EventPipe IDs are assigned by the reader. Use the declared event names.
    switch (data.EventName)
    {
        case "PhaseStarted": started.Add((data.ProcessID, Number("operation"))); break;
        case "PhaseCompleted":
            completed.Add(new(data.ProcessID, (int)Number("phase"), Number("operation"),
                Number("elapsedMicroseconds"), Number("succeeded") != 0, Number("rows"),
                Number("logicalBytes"), Number("committedTransactions"), Number("cacheHits")));
            break;
        case "CacheInvalidated":
            var key = (data.ProcessID, (int)Number("phase"), (int)Number("reason"));
            invalidations[key] = invalidations.GetValueOrDefault(key) + 1;
            break;
        case "EventSourceMessage": sourceErrors++; break; // Never copy error/source payloads into a shareable report.
    }
};
source.Process();
if (started.Count == 0 && completed.Count == 0)
{
    Console.Error.WriteLine("No TajsTokens phase events found. This is not evidence of zero work; verify the provider, runtime and capture interval.");
    return 2;
}
var phases = completed.GroupBy(item => (item.Process, item.Phase)).OrderBy(group => group.Key.Process).ThenBy(group => group.Key.Phase)
    .Select(group =>
    {
        double[] times = group.Select(item => item.Microseconds / 1000d).Order().ToArray();
        object Known(Func<Completion, long> select)
        {
            long[] values = group.Select(select).Where(value => value >= 0).ToArray();
            return new { measuredPhases = values.Length, total = values.Length == 0 ? (long?)null : values.Sum() };
        }
        return new
        {
            processId = group.Key.Process,
            phase = Enum.GetName(typeof(WorkPhase), group.Key.Phase) ?? group.Key.Phase.ToString(),
            completed = times.Length,
            unsuccessful = group.Count(item => !item.Succeeded),
            totalElapsedMs = times.Sum(),
            p50Ms = times[(int)Math.Ceiling(.5 * times.Length) - 1],
            p95Ms = times[(int)Math.Ceiling(.95 * times.Length) - 1],
            maxMs = times[^1],
            rows = Known(item => item.Rows),
            logicalBytes = Known(item => item.Bytes),
            committedTransactions = Known(item => item.Commits),
            cacheHits = Known(item => item.Hits),
        };
    }).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    format = "tajstokens.work-phases/v1",
    eventsLost = source.EventsLost,
    sourceErrors,
    started = started.Count,
    completed = completed.Count,
    unfinishedWithinCapture = started.Except(completed.Select(item => (item.Process, item.Operation))).Count(),
    phases,
    invalidations = invalidations.OrderBy(item => item.Key.Process).ThenBy(item => item.Key.Phase).ThenBy(item => item.Key.Reason)
        .Select(item => new { processId = item.Key.Process, phaseId = item.Key.Phase, reason = item.Key.Reason, count = item.Value }),
    qualification = "Nested phases overlap; do not sum them as process wall time or CPU. Unmeasured counters are null, not zero. Rows are phase-specific submitted observations or returned rows, not unique retained evidence. Logical bytes are not physical disk I/O. Unfinished phases may extend outside the capture. First-frame timing begins at the app visibility transition and excludes physical input/IPC delivery. Trace collection adds overhead.",
}, new JsonSerializerOptions { WriteIndented = true }));
return source.EventsLost > 0 || sourceErrors > 0 ? 1 : 0;

internal sealed record Completion(int Process, int Phase, long Operation, long Microseconds,
    bool Succeeded, long Rows, long Bytes, long Commits, long Hits);
