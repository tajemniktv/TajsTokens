using System.Security.Cryptography;
using System.Text;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static partial class WorkloadCompositionPrediction
{
    public const string PathVersion = "recent-joint-block-paths/v1";
    public const int PathCount = 64;
    public const int BinMinutes = 5;
    public const int MaximumBlockBins = 3;
    public const double MaximumPathHorizonHours = 2 + 5d / 60;
    public const uint DefaultPathSeed = 0x544F4B4E;
    public const string PathMethodology = "Conditional finite-window resampling, not a calibrated bootstrap forecast or exhaustion probability. " +
        "64 paths sample whole joint five-minute bins in contiguous runs of up to three bins from the declared recent context. " +
        "Starts are uniform over source bins; runs truncate at the source end rather than wrap to an unobserved adjacency. " +
        "Block boundaries can change longer-range dependence; empty recorded bins are pauses in this local record, not verified idleness. " +
        "Final partial bins scale all joint categories together by elapsed duration. Volume/composition remain paired; no unseen model/category evidence is created. " +
        "Xorshift32 draws use recorded seed/origin; revision hash includes exact available amounts, labels, event/collection clocks and configuration. " +
        "Cache retains at most eight immutable ensembles, independently of quota coefficients, TT basis and prices. " +
        "Context is at most two hours in whole five-minute bins; nominal forecast horizons are at most two hours, allowing the existing five-minute outcome polling tolerance. No fitting, live selection or future-sleep assumption.";

    public sealed class PathCache
    {
        public const int MaximumEntries = 8;
        private readonly object _gate = new();
        private readonly Dictionary<string, JointWorkloadPaths> _entries = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public int Count { get { lock (_gate) return _entries.Count; } }
        internal JointWorkloadPaths GetOrBuild(string key, Func<JointWorkloadPaths> factory)
        {
            lock (_gate) { if (_entries.TryGetValue(key, out var hit)) { Hits++; return hit; } Misses++; }
            var created = factory();
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var raced)) return raced;
                while (_entries.Count >= MaximumEntries) _entries.Remove(_order.Dequeue());
                _entries.Add(key, created); _order.Enqueue(key);
            }
            return created;
        }
    }

    public static JointWorkloadPaths SamplePaths(CodexForecastDataset data, DateTimeOffset origin, double contextHours, double horizonHours,
        ForecastReplayAvailability availability = ForecastReplayAvailability.CollectedByOrigin,
        uint seed = DefaultPathSeed, PathCache? cache = null, CancellationToken token = default)
    {
        ValidatePaths(contextHours, horizonHours, availability);
        var rows = data.Tokens.Where(x => x.ObservedAtUtc > origin.AddHours(-contextHours) && x.ObservedAtUtc <= origin &&
            (availability == ForecastReplayAvailability.ReconstructedEventTime || x.CapturedAtUtc <= origin)).ToArray();
        return SampleWindow(rows, origin, contextHours, horizonHours, availability, seed, cache, token);
    }

    internal static JointWorkloadPaths SamplePaths(CodexForecastFeatureIndex index, DateTimeOffset origin, double contextHours, double horizonHours,
        ForecastReplayAvailability availability, uint seed, PathCache cache, CancellationToken token)
    {
        ValidatePaths(contextHours, horizonHours, availability);
        return SampleWindow(index.TokenWindow(origin, contextHours, availability), origin, contextHours, horizonHours, availability, seed, cache, token);
    }

    private static void ValidatePaths(double contextHours, double horizonHours, ForecastReplayAvailability availability)
    {
        Validate(contextHours, horizonHours, availability);
        if (contextHours > 2 || horizonHours > MaximumPathHorizonHours || TimeSpan.FromHours(horizonHours).Ticks == 0 || contextHours * 60 < BinMinutes ||
            TimeSpan.FromHours(contextHours).Ticks % TimeSpan.FromMinutes(BinMinutes).Ticks != 0)
            throw new ArgumentOutOfRangeException(nameof(contextHours), "Paths use a context up to two hours in whole five-minute bins and a nominal horizon up to two hours with existing polling tolerance.");
    }

    private static JointWorkloadPaths SampleWindow(CodexPredictiveTokenEvent[] rows, DateTimeOffset origin, double contextHours, double horizonHours,
        ForecastReplayAvailability availability, uint seed, PathCache? cache, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long binTicks = TimeSpan.FromMinutes(BinMinutes).Ticks;
        int binCount = (int)(TimeSpan.FromHours(contextHours).Ticks / binTicks);
        uint resolvedSeed = seed ^ (uint)origin.UtcTicks ^ (uint)(origin.UtcTicks >> 32);
        if (resolvedSeed == 0) resolvedSeed = DefaultPathSeed;
        string revision = Revision(rows, origin, contextHours, horizonHours, availability, resolvedSeed);
        JointWorkloadPaths Build()
        {
            var context = ContinueWindow(rows, origin, contextHours, horizonHours, availability).Receipt;
            if (context.Status != "conditional-continuation")
                return new(PathVersion, revision, resolvedSeed, binCount, MaximumBlockBins, context, [], null, PathMethodology);
            DateTimeOffset from = origin.AddHours(-contextHours);
            var groups = rows.GroupBy(x => (int)((x.ObservedAtUtc.UtcTicks - from.UtcTicks - 1) / binTicks)).ToDictionary(x => x.Key, x => x.ToArray());
            var bins = Enumerable.Range(0, binCount).Select(i => CodexJointWorkloadBuilder.CombineConditional(
                [(CodexJointWorkloadBuilder.Build(groups.GetValueOrDefault(i) ?? []), 1m)])).ToArray();
            long horizonTicks = TimeSpan.FromHours(horizonHours).Ticks;
            long lastTicks = horizonTicks % binTicks;
            var partial = lastTicks == 0 ? bins : bins.Select(x => CodexJointWorkloadBuilder.CombineConditional([(x, lastTicks / (decimal)binTicks)])).ToArray();
            var paths = new List<JointWorkloadPath>(PathCount);
            uint state = resolvedSeed;
            for (int p = 0; p < PathCount; p++)
            {
                token.ThrowIfCancellationRequested();
                var steps = new List<JointWorkloadPathStep>(); long elapsed = 0;
                while (elapsed < horizonTicks)
                {
                    int start = Draw(ref state, binCount);
                    for (int offset = 0; offset < Math.Min(MaximumBlockBins, binCount - start) && elapsed < horizonTicks; offset++)
                    {
                        long length = Math.Min(binTicks, horizonTicks - elapsed);
                        int source = start + offset;
                        steps.Add(new(source, offset == 0, origin.AddTicks(elapsed), origin.AddTicks(elapsed + length), length == binTicks ? bins[source] : partial[source]));
                        elapsed += length;
                    }
                }
                paths.Add(new(steps, CodexJointWorkloadBuilder.CombineConditional(steps.Select(x => (x.Workload, 1m)))));
            }
            return new(PathVersion, revision, resolvedSeed, binCount, MaximumBlockBins, context, paths,
                CodexJointWorkloadBuilder.CombineConditional(paths.Select(x => (x.Aggregate, 1m / PathCount))), PathMethodology);
        }
        return cache is null ? Build() : cache.GetOrBuild(revision, Build);
    }

    private static string Revision(CodexPredictiveTokenEvent[] rows, DateTimeOffset origin, double context, double horizon,
        ForecastReplayAvailability availability, uint seed)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(PathVersion); writer.Write(origin.UtcTicks); writer.Write(context); writer.Write(horizon); writer.Write((int)availability); writer.Write(seed);
            writer.Write(rows.Length);
            foreach (var row in rows)
            {
                writer.Write(row.ObservedAtUtc.UtcTicks); writer.Write(row.CapturedAtUtc?.UtcTicks ?? long.MinValue);
                writer.Write(row.Model is not null); if (row.Model is { } model) writer.Write(model);
                writer.Write(row.ReasoningEffort is not null); if (row.ReasoningEffort is { } effort) writer.Write(effort);
                writer.Write(row.UncachedInputTokens); writer.Write(row.CacheReadTokens); writer.Write(row.CacheWriteTokens);
                writer.Write(row.NonReasoningOutputTokens); writer.Write(row.ReasoningOutputTokens); writer.Write(row.ReportedTotalTokens);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static int Draw(ref uint state, int count)
    {
        uint bound = (uint)count, threshold = unchecked(0u - bound) % bound;
        uint value;
        do { state ^= state << 13; state ^= state >> 17; state ^= state << 5; value = state; } while (value < threshold);
        return (int)(value % bound);
    }
}
