using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Per-evaluation index; the existing feature builder still owns every calculation.</summary>
internal sealed class CodexForecastFeatureIndex
{
    private readonly CodexForecastDataset _data;
    private readonly (CodexPredictiveTokenEvent Row, int Ordinal)[] _tokens;
    private readonly DateTimeOffset[] _tokenTimes;
    private readonly CodexContextObservation[] _contexts;
    private readonly DateTimeOffset[] _contextTimes;
    private readonly CodexWorkloadObservation[] _metadata;
    private readonly DateTimeOffset[] _metadataTimes;
    private readonly Dictionary<string, (CodexWorkloadObservation Row, int Ordinal)[]> _sessions;
    private readonly DateTimeOffset[] _lateCollectionTimes;
    private readonly Dictionary<(DateTimeOffset, double, ForecastReplayAvailability, int), CodexForecastFeatures> _features = new();
    internal int BuildCount { get; private set; }

    public CodexForecastFeatureIndex(CodexForecastDataset data)
    {
        using var preparation = WorkDiagnostics.Measure(WorkPhase.FeatureIndexPreparation);
        _data = data;
        _tokens = data.Tokens.Select((row, ordinal) => (Row: row, Ordinal: ordinal)).OrderBy(x => x.Row.ObservedAtUtc).ToArray();
        _tokenTimes = _tokens.Select(x => x.Row.ObservedAtUtc).ToArray();
        _contexts = data.Context.OrderBy(x => x.ObservedAtUtc).ToArray();
        _contextTimes = _contexts.Select(x => x.ObservedAtUtc).ToArray();
        _metadata = data.Workload.Where(x => x.ObservedAtUtc is not null).OrderBy(x => x.ObservedAtUtc).ToArray();
        _metadataTimes = _metadata.Select(x => x.ObservedAtUtc!.Value).ToArray();
        _sessions = data.Workload.Select((row, ordinal) => (Row: row, Ordinal: ordinal))
            .GroupBy(x => x.Row.SessionId).ToDictionary(x => x.Key, x => x.ToArray());
        _lateCollectionTimes = data.Tokens.Where(x => x.CapturedAtUtc > x.ObservedAtUtc).Select(x => x.CapturedAtUtc!.Value)
            .Concat(data.Workload.Where(x => x.CapturedAtUtc > x.ObservedAtUtc).Select(x => x.CapturedAtUtc))
            .Concat(data.Context.Where(x => x.CapturedAtUtc > x.ObservedAtUtc).Select(x => x.CapturedAtUtc!.Value))
            .Distinct().Order().ToArray();
        preparation?.Complete(rows: data.Tokens.Count + data.Workload.Count + data.Context.Count, cacheHits: 0);
    }

    public CodexForecastFeatures Build(DateTimeOffset origin, ForecastReplayAvailability availability)
        => Build(origin, 2, availability);

    public CodexForecastFeatures Build(DateTimeOffset origin, double lookbackHours, ForecastReplayAvailability availability)
        => BuildCore(origin, lookbackHours, availability,
            availability == ForecastReplayAvailability.CollectedByOrigin ? origin : null);

    internal CodexPredictiveTokenEvent[] TokenWindow(DateTimeOffset origin, double contextHours, ForecastReplayAvailability availability)
    {
        if (!double.IsFinite(contextHours) || contextHours <= 0 || contextHours > 168) throw new ArgumentOutOfRangeException(nameof(contextHours));
        return _tokens[Through(_tokenTimes, origin.AddHours(-contextHours))..Through(_tokenTimes, origin)]
            .Where(x => availability == ForecastReplayAvailability.ReconstructedEventTime || x.Row.CapturedAtUtc <= origin)
            .OrderBy(x => x.Ordinal).Select(x => x.Row).ToArray();
    }

    // Token replay has always reconstructed historical features from evidence available at
    // the enclosing prediction origin. Do not silently substitute each historical origin's
    // collection cutoff, which would change its training contract.
    internal CodexForecastFeatures BuildReconstructedPrefix(DateTimeOffset origin, DateTimeOffset? collectedBy)
    {
        if (collectedBy < origin) throw new ArgumentOutOfRangeException(nameof(collectedBy));
        return BuildCore(origin, 2, ForecastReplayAvailability.ReconstructedEventTime, collectedBy);
    }

    private CodexForecastFeatures BuildCore(DateTimeOffset origin, double lookbackHours,
        ForecastReplayAvailability availability, DateTimeOffset? collectedBy)
    {
        if (!double.IsFinite(lookbackHours) || lookbackHours <= 0 || lookbackHours > 168)
            throw new ArgumentOutOfRangeException(nameof(lookbackHours));
        // Timely observations cannot change an earlier feature when the enclosing cutoff
        // advances. Only newly available late evidence can do so. Unknown capture remains
        // excluded in collection-qualified mode. This conservative late-evidence generation
        // may miss reuse, but never admits a not-yet-collected row.
        int generation = collectedBy is { } cutoff ? Through(_lateCollectionTimes, cutoff) : -1;
        var key = (origin, lookbackHours, availability, generation);
        if (_features.TryGetValue(key, out CodexForecastFeatures? cached)) return cached;
        bool Available(DateTimeOffset? captured) => collectedBy is null || captured <= collectedBy;
        DateTimeOffset from = origin.AddHours(-lookbackHours);
        CodexPredictiveTokenEvent[] tokens = _tokens[Through(_tokenTimes, from)..Through(_tokenTimes, origin)]
            .Where(x => Available(x.Row.CapturedAtUtc)).OrderBy(x => x.Ordinal).Select(x => x.Row).ToArray();
        var sessions = new HashSet<string>(tokens.Select(x => x.SessionId), StringComparer.Ordinal);
        for (int i = Through(_metadataTimes, from), end = Through(_metadataTimes, origin); i < end; i++)
            if (Available(_metadata[i].CapturedAtUtc)) sessions.Add(_metadata[i].SessionId);

        // A turn contributes only if its terminal or another event is in the lookback. Keep
        // complete prefixes for those sessions and token owners, including older starts and
        // ownership metadata. Preserve input order for stable timestamp ties.
        CodexWorkloadObservation[] metadata = sessions.Where(_sessions.ContainsKey).SelectMany(x => _sessions[x])
            .Where(x => x.Row.ObservedAtUtc <= origin && Available(x.Row.CapturedAtUtc))
            .OrderBy(x => x.Ordinal).Select(x => x.Row).ToArray();
        CodexForecastFeatures features = CodexForecastFeatureBuilder.Build(_data with
        {
            Tokens = tokens,
            Workload = metadata,
            Context = _contexts[Through(_contextTimes, from)..Through(_contextTimes, origin)]
                .Where(x => Available(x.CapturedAtUtc)).ToArray(),
        }, origin, lookbackHours, availability);
        BuildCount++;
        // One evaluation may span many origins. Bound retained numeric features without
        // truncating supported history or changing answers after the cache fills.
        if (_features.Count < 8192) _features.Add(key, features);
        return features;
    }

    private static int Through(DateTimeOffset[] times, DateTimeOffset at)
    {
        int low = 0, high = times.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (times[middle] <= at) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
