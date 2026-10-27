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
    private readonly Dictionary<(DateTimeOffset, double, ForecastReplayAvailability), CodexForecastFeatures> _features = new();

    public CodexForecastFeatureIndex(CodexForecastDataset data)
    {
        _data = data;
        _tokens = data.Tokens.Select((row, ordinal) => (Row: row, Ordinal: ordinal)).OrderBy(x => x.Row.ObservedAtUtc).ToArray();
        _tokenTimes = _tokens.Select(x => x.Row.ObservedAtUtc).ToArray();
        _contexts = data.Context.OrderBy(x => x.ObservedAtUtc).ToArray();
        _contextTimes = _contexts.Select(x => x.ObservedAtUtc).ToArray();
        _metadata = data.Workload.Where(x => x.ObservedAtUtc is not null).OrderBy(x => x.ObservedAtUtc).ToArray();
        _metadataTimes = _metadata.Select(x => x.ObservedAtUtc!.Value).ToArray();
        _sessions = data.Workload.Select((row, ordinal) => (Row: row, Ordinal: ordinal))
            .GroupBy(x => x.Row.SessionId).ToDictionary(x => x.Key, x => x.ToArray());
    }

    public CodexForecastFeatures Build(DateTimeOffset origin, ForecastReplayAvailability availability)
        => Build(origin, 2, availability);

    public CodexForecastFeatures Build(DateTimeOffset origin, double lookbackHours, ForecastReplayAvailability availability)
    {
        if (!double.IsFinite(lookbackHours) || lookbackHours <= 0 || lookbackHours > 168)
            throw new ArgumentOutOfRangeException(nameof(lookbackHours));
        if (_features.TryGetValue((origin, lookbackHours, availability), out CodexForecastFeatures? cached)) return cached;
        DateTimeOffset from = origin.AddHours(-lookbackHours);
        CodexPredictiveTokenEvent[] tokens = _tokens[Through(_tokenTimes, from)..Through(_tokenTimes, origin)]
            .OrderBy(x => x.Ordinal).Select(x => x.Row).ToArray();
        var sessions = new HashSet<string>(tokens.Select(x => x.SessionId), StringComparer.Ordinal);
        for (int i = Through(_metadataTimes, from), end = Through(_metadataTimes, origin); i < end; i++)
            sessions.Add(_metadata[i].SessionId);

        // A turn contributes only if its terminal or another event is in the lookback. Keep
        // complete prefixes for those sessions and token owners, including older starts and
        // ownership metadata. Preserve input order for stable timestamp ties.
        CodexWorkloadObservation[] metadata = sessions.Where(_sessions.ContainsKey).SelectMany(x => _sessions[x])
            .Where(x => x.Row.ObservedAtUtc <= origin).OrderBy(x => x.Ordinal).Select(x => x.Row).ToArray();
        CodexForecastFeatures features = CodexForecastFeatureBuilder.Build(_data with
        {
            Tokens = tokens,
            Workload = metadata,
            Context = _contexts[Through(_contextTimes, from)..Through(_contextTimes, origin)],
        }, origin, lookbackHours, availability);
        _features.Add((origin, lookbackHours, availability), features);
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
