// Taj's Tokens | TokenWorkloadPredictionService.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Core.Services;

/// <summary>
///     Learns recorded local token consumption directly from rollouts, independently of quota labels.
///     Historical features are event-time reconstructions, not claims about past deployment availability.
/// </summary>
public static class TokenWorkloadPredictionService
{
    public const string PolicyVersion = "rollout-token/v3-joint-composition";

    public const string Methodology = "Reconstructed rollout history: predict the sum of recorded token increments " +
                                      "(including cached input) in the next interval, conditional on recent local activity. " +
                                      "Chronological held-out outcomes; future tokens, completions and model changes are excluded from inputs. " +
                                      "A zero target means no tokens recorded in that interval, not proof of no account activity. " +
                                      "This installation's corpus is not the entire account; no subscription-quota or money conversion is implied. " +
                                      "Backfilled evidence is usable for retrospective learning, not proof that an earlier app had collected it.";

    private static readonly string[] Candidates = ["recent-30m", "recent-2h", "recent-median", "workload-ridge", "workload-linear"];
    private const int TrainingHistory = 120;
    private const int ValidationHistory = 48;

    public static TokenHorizonPrediction? PredictHorizon(
        CodexForecastDataset data,
        DateTimeOffset origin,
        double horizon,
        ForecastReplayAvailability availability = ForecastReplayAvailability.ReconstructedEventTime,
        CancellationToken cancellationToken = default)
        => PredictHorizonCore(data, origin, horizon, availability, cancellationToken, null);

    internal static TokenHorizonPrediction? PredictHorizonCached(
        CodexForecastDataset data, DateTimeOffset origin, double horizon,
        ForecastReplayAvailability availability, CancellationToken cancellationToken, ReplayCache cache)
        => PredictHorizonCore(data, origin, horizon, availability, cancellationToken, cache);

    // Retain the exhaustive path as the numerical oracle for dependency-window validation.
    internal static TokenHorizonPrediction? PredictHorizonExhaustive(
        CodexForecastDataset data, DateTimeOffset origin, double horizon,
        ForecastReplayAvailability availability, CancellationToken cancellationToken = default)
        => PredictHorizonCore(data, origin, horizon, availability, cancellationToken, null, currentOnly: false);

    private static TokenHorizonPrediction? PredictHorizonCore(
        CodexForecastDataset data, DateTimeOffset origin, double horizon,
        ForecastReplayAvailability availability, CancellationToken cancellationToken, ReplayCache? cache, bool currentOnly = true)
    {
        CodexForecastFeatureIndex? preparedFeatures = null;
        CodexForecastDataset prefix;
        if (cache is not null)
            (prefix, preparedFeatures) = cache.Prepare(data, origin, availability);
        else
        {
            prefix = data with
            {
                Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= origin).ToArray(),
                Workload = data.Workload.Where(x => x.ObservedAtUtc <= origin).ToArray(),
                Context = data.Context.Where(x => x.ObservedAtUtc <= origin).ToArray(),
            };
            if (availability == ForecastReplayAvailability.CollectedByOrigin)
                prefix = prefix with
                {
                    Tokens = prefix.Tokens.Where(x => x.CapturedAtUtc <= origin).ToArray(),
                    Workload = prefix.Workload.Where(x => x.CapturedAtUtc <= origin).ToArray(),
                    Context = prefix.Context.Where(x => x.CapturedAtUtc <= origin).ToArray(),
                };
        }
        if (!prefix.Tokens.Any(x => x.ObservedAtUtc > origin.AddHours(-2))) return null;
        if (horizon <= .25 && !CodexNowcastActivity.Evaluate(prefix, origin, availability).SupportsNowcast) return null;
        TokenHorizonPrediction? prediction = Evaluate(prefix, horizon, origin, cancellationToken, cache, preparedFeatures, currentOnly).Current;
        return prediction is null
            ? null
            : prediction with { Composition = WorkloadCompositionPrediction.Project(data, origin, prediction, availability) };
    }

    public static TokenWorkloadForecast Predict(
        CodexForecastDataset data,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        CodexForecastDataset available = Available(data, now);
        DateTimeOffset? last = available.Tokens.Count > 0 ? available.Tokens.Max(x => x.ObservedAtUtc) : null;
        var predictions = new List<TokenHorizonPrediction>();
        NowcastActivity activity = CodexNowcastActivity.Evaluate(available, now);
        if (activity.SupportsNowcast && last is not null && now - last <= TimeSpan.FromHours(2))
            foreach (double horizon in new[] { 5d / 60, .25 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) result = Evaluate(
                    available,
                    horizon,
                    now,
                    cancellationToken, currentOnly: true);
                if (result.Current is not null)
                    predictions.Add(
                        result.Current with { Composition = WorkloadCompositionPrediction.Project(available, now, result.Current) });
            }
        if (activity.SupportsNowcast && predictions.Count == 0)
            activity = activity with { Explanation = activity.Explanation + " Insufficient recent token history to anchor a nowcast." };
        return new TokenWorkloadForecast(
            now,
            last,
            available.Tokens.Count,
            available.Tokens.Select(x => x.SessionId).Distinct().Count(),
            predictions,
            Methodology + " Live nowcasts use a ten-minute observed-activity gate, not a liveness guarantee. " + activity.Explanation)
        {
            Activity = activity,
            SessionOutlooks = activity.SupportsNowcast
                ? SessionWorkloadPredictionService.Evaluate(available, now, cancellationToken).Current
                : [],
        };
    }

    public static IReadOnlyList<TokenForecastTrial> Replay(
        CodexForecastDataset data,
        double horizon,
        CancellationToken cancellationToken = default)
    {
        return Evaluate(data, horizon, null, cancellationToken).Trials
            .Select(x => x with
            {
                Prediction = x.Prediction with { Composition = WorkloadCompositionPrediction.Project(data, x.OriginUtc, x.Prediction) },
            }).ToArray();
    }

    public static IReadOnlyList<TokenForecastScore> EvaluateScores(
        CodexForecastDataset data,
        CancellationToken cancellationToken = default)
    {
        var scores = new List<TokenForecastScore>();
        foreach (double horizon in new[] { 5d / 60, .25, 0.5, 2d })
        {
            (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) evaluated = Evaluate(
                data,
                horizon,
                null,
                cancellationToken);
            foreach (string candidate in Candidates.Append("zero-workload").Append(PolicyVersion))
            {
                // Candidate predictions are exposed by the same evaluation pass, not refitted on outcomes.
                double[] errors = evaluated.Points.Select(x => (candidate == PolicyVersion
                    ? x.Prediction.ExpectedTokens
                    : x.Estimates.GetValueOrDefault(candidate, x.Estimates["recent-30m"])) - x.Row.Target).ToArray();
                TokenForecastTrial[] bands = candidate == PolicyVersion
                    ? evaluated.Trials.Where(x => x.Prediction.LowerTokens is not null).ToArray()
                    : [];
                scores.Add(
                    new TokenForecastScore(
                        horizon,
                        candidate,
                        evaluated.Trials.Count,
                        errors.Length > 0 ? errors.Average(Math.Abs) : null,
                        errors.Length > 0 ? Math.Sqrt(errors.Average(x => x * x)) : null,
                        bands.Length,
                        bands.Length > 0
                            ? bands.Average(x => x.ObservedTokens >= x.Prediction.LowerTokens &&
                                                 x.ObservedTokens <= x.Prediction.UpperTokens
                                ? 1d
                                : 0d)
                            : null,
                        candidate == PolicyVersion
                            ? evaluated.Points.Count(x => x.Prediction.Model.StartsWith("workload-", StringComparison.Ordinal))
                            : candidate.StartsWith("workload-", StringComparison.Ordinal)
                                ? evaluated.Points.Count(x => x.Estimates.ContainsKey(candidate))
                                : 0));
            }
        }
        return scores;
    }

    private static (IReadOnlyList<TokenForecastTrial> Trials, TokenHorizonPrediction? Current, IReadOnlyList<Point> Points) Evaluate(
        CodexForecastDataset data,
        double horizon,
        DateTimeOffset? now,
        CancellationToken cancellationToken,
        ReplayCache? cache = null,
        CodexForecastFeatureIndex? preparedFeatures = null, bool currentOnly = false)
    {
        if (!double.IsFinite(horizon) || horizon <= 0 || horizon > 24 || TimeSpan.FromHours(horizon).Ticks == 0)
            throw new ArgumentOutOfRangeException(nameof(horizon));
        CodexPredictiveTokenEvent[] tokens = data.Tokens.Where(x => x.ReportedTotalTokens >= 0).OrderBy(x => x.ObservedAtUtc).ToArray();
        if (tokens.Length < 2) return ([], null, []);
        decimal[] sums = new decimal[tokens.Length + 1];
        for (int i = 0; i < tokens.Length; i++) sums[i + 1] = sums[i] + tokens[i].ReportedTotalTokens;
        CodexWorkloadObservation[] workload = data.Workload.Where(x => x.ObservedAtUtc is not null).OrderBy(x => x.ObservedAtUtc).ToArray();
        DateTimeOffset[] workloadTimes = workload.Select(x => x.ObservedAtUtc!.Value).ToArray();
        DateTimeOffset[] activityTimes = tokens.Where(x => x.ReportedTotalTokens > 0).Select(x => x.ObservedAtUtc)
            .Concat(workload.Where(x => CodexNowcastActivity.IsActivityEvent(x.EventType)).Select(x => x.ObservedAtUtc!.Value)).Order()
            .ToArray();
        Dictionary<string, CodexWorkloadObservation[]> bySession =
            workload.GroupBy(x => x.SessionId).ToDictionary(x => x.Key, x => x.ToArray());
        CodexContextObservation[] contexts = data.Context.OrderBy(x => x.ObservedAtUtc).ToArray();
        DateTimeOffset[] contextTimes = contexts.Select(x => x.ObservedAtUtc).ToArray();

        int Through(DateTimeOffset at)
        {
            int low = 0;
            int high = tokens.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (tokens[middle].ObservedAtUtc <= at) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        double Window(DateTimeOffset from, DateTimeOffset to)
        {
            return (double)(sums[Through(to)] - sums[Through(from)]);
        }

        CodexForecastFeatures Features(DateTimeOffset at)
        {
            // The index is qualified by this replay's collection cutoff. Historical features
            // retain the existing reconstructed semantics within that available prefix.
            if (preparedFeatures is not null) return preparedFeatures.BuildReconstructedPrefix(at, cache!.CollectedBy);
            DateTimeOffset from = at.AddHours(-2);
            CodexPredictiveTokenEvent[] recentTokens = tokens[Through(from)..Through(at)];
            CodexWorkloadObservation[] recentWorkload = workload[UpperBound(workloadTimes, from)..UpperBound(workloadTimes, at)];
            IEnumerable<string> sessions = recentTokens.Select(x => x.SessionId).Concat(recentWorkload.Select(x => x.SessionId)).Distinct();
            // Keep complete prefixes for relevant sessions, including starts before the lookback.
            // Old inactive sessions cannot affect these features. The owner still applies the time cutoff.
            CodexForecastDataset local = data with
            {
                Tokens = recentTokens,
                Workload = sessions.Where(bySession.ContainsKey).SelectMany(x => bySession[x]).ToArray(),
                Context = contexts[UpperBound(contextTimes, from)..UpperBound(contextTimes, at)],
            };
            return CodexForecastFeatureBuilder.Build(local, at);
        }

        var rows = new List<Row>();
        var origins = new List<RowOrigin>();
        DateTimeOffset first = tokens[0].ObservedAtUtc.AddHours(2);
        DateTimeOffset last = tokens[^1].ObservedAtUtc;
        // Non-overlapping target windows; no quota reset epochs are necessary for native token targets.
        TimeSpan step = TimeSpan.FromHours(horizon);
        var origin = new DateTimeOffset((first.UtcTicks / step.Ticks + 1) * step.Ticks, TimeSpan.Zero);
        for (; origin.AddHours(horizon) <= last; origin += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Do not rebuild turn metadata for months of idle grid points between sessions.
            if (Through(origin) == Through(origin.AddHours(-2))) continue;
            if (horizon <= .25)
            {
                int throughActivity = UpperBound(activityTimes, origin);
                if (throughActivity == 0 || origin - activityTimes[throughActivity - 1] >= CodexNowcastActivity.IdleAfter) continue;
            }
            DateTimeOffset end = origin.AddHours(horizon);
            double target = Window(origin, end);
            if (currentOnly)
            {
                // The Through guard proves a token is present in this reconstructed prefix.
                // Defer expensive features until the prediction's dependency closure is known.
                origins.Add(new(origin, end, Recent(origin), target));
                continue;
            }
            CodexForecastFeatures features = Features(origin);
            if (features.ObservedTokenEvents == 0) continue;
            var row = new Row(origin, end, features, Recent(origin), target);
            rows.Add(row);
        }
        var points = new List<Point>();
        IEnumerable<Row> predictionRows = rows;
        if (currentOnly && now is { } currentOrigin)
        {
            // Candidate estimates depend only on their 120 matured training rows. Selection
            // needs 48 earlier candidate estimates; the current band additionally needs the
            // selected predictions of those 48 points. One more validation window supplies
            // their estimates. Preserve every required training row, but avoid fitting the rest of
            // history merely to answer one origin. Replay/score reports remain exhaustive.
            RowOrigin[] validation = origins.Where(x => x.End <= currentOrigin).TakeLast(ValidationHistory).ToArray();
            if (validation.Length == 0) predictionRows = [];
            else
            {
                RowOrigin[] warmup = origins.Where(x => x.End <= validation[0].Origin).TakeLast(ValidationHistory).ToArray();
                DateTimeOffset firstPredictionOrigin = (warmup.Length > 0 ? warmup[0] : validation[0]).Origin;
                RowOrigin[] training = origins.Where(x => x.End <= firstPredictionOrigin && x.Origin < firstPredictionOrigin)
                    .TakeLast(TrainingHistory).ToArray();
                DateTimeOffset firstFeatureOrigin = training.Length > 0 ? training[0].Origin : origins[0].Origin;
                foreach (RowOrigin item in origins.Where(x => x.Origin >= firstFeatureOrigin))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CodexForecastFeatures features = Features(item.Origin);
                    // Preserve the exhaustive answer if a future feature-eligibility policy
                    // differs from the token-presence guard used to construct these origins.
                    if (features.ObservedTokenEvents == 0)
                        return Evaluate(data, horizon, now, cancellationToken, cache, preparedFeatures, currentOnly: false);
                    rows.Add(new(item.Origin, item.End, features, item.Recent, item.Target));
                }
                predictionRows = rows.Where(x => x.Origin >= firstPredictionOrigin);
            }
        }
        foreach (Row row in predictionRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            points.Add(PredictAt(row));
        }
        TokenHorizonPrediction? current = null;
        if (now is { } at && at - tokens[0].ObservedAtUtc >= TimeSpan.FromHours(2))
        {
            CodexForecastFeatures features = Features(at);
            var row = new Row(at, at.AddHours(horizon), features, Recent(at), 0);
            current = PredictAt(row).Prediction;
        }
        return (currentOnly ? [] : points.Select(x => new TokenForecastTrial(x.Row.Origin, x.Row.End, x.Row.Target, x.Prediction)).ToArray(), current, points);

        double Recent(DateTimeOffset at)
        {
            return Window(at.AddMinutes(-30), at) * horizon / 0.5;
        }

        Point PredictAt(Row row)
        {
            Row[] training = rows.Where(x => x.End <= row.Origin && x.Origin < row.Origin).TakeLast(TrainingHistory).ToArray();
            Point[] validation = points.Where(x => x.Row.End <= row.Origin).TakeLast(ValidationHistory).ToArray();

            IReadOnlyDictionary<string, double> ReadEstimates()
            {
                string? key = cache?.EstimateKey(row, training, horizon);
                if (key is not null && cache!.TryGetEstimates(key, out var existing)) return existing!;
                var estimates = new Dictionary<string, double>
                {
                    ["zero-workload"] = 0, // Evaluation-only inactivity baseline, not a fitted activity classifier.
                    ["recent-30m"] = row.Recent,
                    ["recent-2h"] = row.Features.Tokens * horizon / 2,
                    ["recent-median"] = training.Length > 0 ? Median(training.TakeLast(8).Select(x => x.Target)) : row.Recent,
                };
                string[] models = training.SelectMany(x => x.Features.ModelTokenShares.Keys).Distinct().Order().Take(8).ToArray();
                string[] efforts = training.SelectMany(x => x.Features.EffortTokenShares.Keys).Distinct().Order().Take(8).ToArray();
                bool supported = row.Features.ModelTokenShares.Count > 0 && row.Features.EffortTokenShares.Count > 0 &&
                                 !row.Features.ModelTokenShares.Keys.Except(models).Any() &&
                                 !row.Features.EffortTokenShares.Keys.Except(efforts).Any();
                AccountLocalRidge? fit = training.Length >= 20 && supported
                    ? AccountLocalRidge.Fit(
                        training.Select(x => Vector(x, models, efforts)).ToArray(),
                        training.Select(x => Math.Log(1 + x.Target)).ToArray(),
                        10)
                    : null;
                if (fit is not null)
                {
                    // Bound extrapolation to twice the largest observed training target, not a made-up quota conversion.
                    double limit = Math.Max(1, training.Max(x => x.Target) * 2);
                    estimates["workload-ridge"] = Math.Clamp(
                        Math.Exp(Math.Clamp(fit.Predict(Vector(row, models, efforts)), 0, Math.Log(1 + limit))) - 1,
                        0,
                        limit);
                    AccountLocalRidge? linear = AccountLocalRidge.Fit(
                        training.Select(x => Vector(x, models, efforts)).ToArray(),
                        training.Select(x => x.Target - x.Recent).ToArray(),
                        100);
                    if (linear is not null)
                        estimates["workload-linear"] = Math.Clamp(row.Recent + linear.Predict(Vector(row, models, efforts)), 0, limit);
                }
                if (key is not null) cache!.StoreEstimates(key, estimates);
                return estimates;
            }
            IReadOnlyDictionary<string, double> estimates = ReadEstimates();
            string selected = horizon >= 2 && training.Length >= 8 ? "recent-median" : "recent-30m";

            double Error(string model, IEnumerable<Point> source)
            {
                return source.Average(x => Math.Abs(x.Estimates[model] - x.Row.Target));
            }

            foreach (string candidate in Candidates)
            {
                if (!estimates.ContainsKey(candidate)) continue;
                Point[] compatible = validation.Where(x => x.Estimates.ContainsKey(candidate) && x.Estimates.ContainsKey(selected))
                    .ToArray();
                if (compatible.Length >= 16 && Error(candidate, compatible) < Error(selected, compatible) * 0.8) selected = candidate;
            }
            double predicted = estimates[selected];
            double[] selectedErrors = validation.Select(x => Math.Abs(x.Prediction.ExpectedTokens - x.Row.Target)).Order().ToArray();
            double? radius = selectedErrors.Length >= 20
                ? selectedErrors[Math.Min(selectedErrors.Length - 1, (int)Math.Ceiling((selectedErrors.Length + 1) * 0.8) - 1)]
                : null;
            Point[] applicable = validation.Where(x => x.Estimates.ContainsKey(selected)).ToArray();
            var point = new Point(
                row,
                estimates,
                new TokenHorizonPrediction(
                    horizon,
                    predicted,
                    selected,
                    training.Length,
                    applicable.Length,
                    applicable.Length > 0 ? Error(selected, applicable) : null,
                    radius is { } r ? Math.Max(0, predicted - r) : null,
                    radius is { } upper ? predicted + upper : null,
                    $"{training.Length} matured training intervals; {applicable.Length} held-out comparisons. " +
                    (selected.StartsWith("workload-", StringComparison.Ordinal)
                        ? "Model/effort/token/activity regression earned selection."
                        : "The simpler forecast currently wins or regression is still learning.") +
                    (radius is null
                        ? " Uncertainty is learning."
                        : " Empirical 80%-target range from earlier selected-policy errors; not a guarantee.")));
            return point;
        }
    }

    private static double[] Vector(Row row, string[] models, string[] efforts)
    {
        CodexForecastFeatures f = row.Features;
        var result = new List<double>
        {
            Math.Log(1 + f.Tokens),
            Math.Log(1 + row.Recent),
            f.CacheReadShare ?? 0,
            f.ReasoningOutputShare ?? 0,
            f.ObservedTokenEvents,
            f.TokenActiveRootSessions,
            f.TokenActiveSubagentSessions,
            f.TokenActiveUnknownSessions,
            f.ObservedOpenTurns,
            f.CompletedTurns,
            f.CompletedTurnWallHours,
            f.Compactions,
            f.LastInputWindowRatio ?? 0,
            f.PeakObservedTurnOverlap,
        };
        result.AddRange(models.Select(x => f.ModelTokenShares.GetValueOrDefault(x)));
        result.AddRange(efforts.Select(x => f.EffortTokenShares.GetValueOrDefault(x)));
        return result.ToArray();
    }

    private static CodexForecastDataset Available(CodexForecastDataset data, DateTimeOffset now)
    {
        return data with
        {
            Tokens = data.Tokens.Where(x => x.ObservedAtUtc <= now && (x.CapturedAtUtc is null || x.CapturedAtUtc <= now)).ToArray(),
            Workload = data.Workload.Where(x => x.ObservedAtUtc <= now && x.CapturedAtUtc <= now).ToArray(),
            Context = data.Context.Where(x => x.ObservedAtUtc <= now && (x.CapturedAtUtc is null || x.CapturedAtUtc <= now)).ToArray(),
        };
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static int UpperBound(DateTimeOffset[] values, DateTimeOffset at)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (values[middle] <= at) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private readonly record struct RowOrigin(DateTimeOffset Origin, DateTimeOffset End, double Recent, double Target);

    internal sealed record Row(DateTimeOffset Origin, DateTimeOffset End, CodexForecastFeatures Features, double Recent, double Target);

    internal sealed record Point(Row Row, IReadOnlyDictionary<string, double> Estimates, TokenHorizonPrediction Prediction);

    /// <summary>
    /// Evaluation-local sorted evidence and numeric features. Current prediction fits use only
    /// their dependency closure; incomplete warmup Point values are never cached. No cross-refresh retention.
    /// </summary>
    internal sealed class ReplayCache
    {
        private readonly Dictionary<CodexForecastFeatures, int> _featureIds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _featureValues = new(StringComparer.Ordinal);
        private readonly Dictionary<RowKey, int> _rowIds = [];
        private readonly Dictionary<string, IReadOnlyDictionary<string, double>> _estimates = new(StringComparer.Ordinal);
        private CodexForecastDataset? _source;
        private PreparedHistory? _prepared;
        internal int PreparationBuilds { get; private set; }
        internal int FeatureBuilds => _prepared?.Features.BuildCount ?? 0;
        internal DateTimeOffset? CollectedBy { get; private set; }

        internal (CodexForecastDataset Prefix, CodexForecastFeatureIndex Features) Prepare(
            CodexForecastDataset data, DateTimeOffset origin, ForecastReplayAvailability availability)
        {
            // Scope-local immutable inputs only. A different source list invalidates preparation;
            // no timestamp/count-only key and no raw dataset survives a worker evaluation.
            if (_source is null || !ReferenceEquals(_source.Tokens, data.Tokens) ||
                !ReferenceEquals(_source.Workload, data.Workload) || !ReferenceEquals(_source.Context, data.Context))
            {
                _source = data;
                _prepared = null;
                _featureIds.Clear(); _featureValues.Clear(); _rowIds.Clear(); _estimates.Clear();
            }
            if (_prepared is null)
            {
                _prepared = new PreparedHistory(data with
                {
                    Tokens = data.Tokens.OrderBy(x => x.ObservedAtUtc).ToArray(),
                    Workload = data.Workload.Where(x => x.ObservedAtUtc is not null)
                        .OrderBy(x => x.ObservedAtUtc).ToArray(),
                    Context = data.Context.OrderBy(x => x.ObservedAtUtc).ToArray(),
                });
                PreparationBuilds++;
            }
            CollectedBy = availability == ForecastReplayAvailability.CollectedByOrigin ? origin : null;
            return (_prepared.Prefix(data, origin, CollectedBy is not null), _prepared.Features);
        }

        // Feature objects are immutable and scoped to the prepared index. A reference fast
        // path shares lossless feature-value identities even across equivalent late-evidence
        // generations. Compact exact row keys replace retained JSON rows. Estimates do not depend on
        // prior selections or bands, so partially initialized warmup Point values cannot leak.
        private readonly record struct RowKey(DateTimeOffset Origin, DateTimeOffset End, int Feature, double Recent, double Target);

        private int RowId(Row row)
        {
            if (!_featureIds.TryGetValue(row.Features, out int feature))
            {
                string value = JsonSerializer.Serialize(row.Features);
                if (!_featureValues.TryGetValue(value, out feature))
                {
                    if (_featureValues.Count >= 8192) return 0;
                    _featureValues.Add(value, feature = _featureValues.Count + 1);
                }
                // Clearing the bounded fast path does not recycle value/row identities.
                if (_featureIds.Count >= 2048) _featureIds.Clear();
                _featureIds.Add(row.Features, feature);
            }
            var key = new RowKey(row.Origin, row.End, feature, row.Recent, row.Target);
            if (_rowIds.TryGetValue(key, out int id)) return id;
            if (_rowIds.Count >= 8192) return 0;
            _rowIds.Add(key, id = _rowIds.Count + 1);
            return id;
        }

        internal string? EstimateKey(Row row, Row[] training, double horizon)
        {
            int current = RowId(row);
            if (current == 0) return null;
            int[] ids = training.Select(RowId).ToArray();
            if (ids.Contains(0)) return null;
            return horizon.ToString("R", CultureInfo.InvariantCulture) + ":" + current + ":" + string.Join(',', ids);
        }

        internal bool TryGetEstimates(string key, out IReadOnlyDictionary<string, double>? estimates)
            => _estimates.TryGetValue(key, out estimates);

        internal void StoreEstimates(string key, IReadOnlyDictionary<string, double> estimates)
        {
            if (_estimates.Count < 4096) _estimates.TryAdd(key, estimates);
        }

        private sealed class PreparedHistory
        {
            private readonly CodexPredictiveTokenEvent[] _tokens;
            private readonly CodexWorkloadObservation[] _workload;
            private readonly CodexContextObservation[] _context;
            private readonly DateTimeOffset[] _tokenTimes, _workloadTimes, _contextTimes;
            private readonly bool _tokensTimely, _workloadTimely, _contextTimely;
            internal CodexForecastFeatureIndex Features { get; }

            internal PreparedHistory(CodexForecastDataset data)
            {
                _tokens = (CodexPredictiveTokenEvent[])data.Tokens;
                _workload = (CodexWorkloadObservation[])data.Workload;
                _context = (CodexContextObservation[])data.Context;
                _tokenTimes = _tokens.Select(x => x.ObservedAtUtc).ToArray();
                _workloadTimes = _workload.Select(x => x.ObservedAtUtc!.Value).ToArray();
                _contextTimes = _context.Select(x => x.ObservedAtUtc).ToArray();
                _tokensTimely = _tokens.All(x => x.CapturedAtUtc <= x.ObservedAtUtc);
                _workloadTimely = _workload.All(x => x.CapturedAtUtc <= x.ObservedAtUtc);
                _contextTimely = _context.All(x => x.CapturedAtUtc <= x.ObservedAtUtc);
                Features = new CodexForecastFeatureIndex(data with
                {
                    Tokens = _tokens.Where(x => x.ReportedTotalTokens >= 0).ToArray(),
                });
            }

            internal CodexForecastDataset Prefix(CodexForecastDataset data, DateTimeOffset origin, bool collected)
            {
                IReadOnlyList<CodexPredictiveTokenEvent> tokens = new ArraySegment<CodexPredictiveTokenEvent>(_tokens, 0, UpperBound(_tokenTimes, origin));
                IReadOnlyList<CodexWorkloadObservation> workload = new ArraySegment<CodexWorkloadObservation>(_workload, 0, UpperBound(_workloadTimes, origin));
                IReadOnlyList<CodexContextObservation> context = new ArraySegment<CodexContextObservation>(_context, 0, UpperBound(_contextTimes, origin));
                return data with
                {
                    Tokens = collected && !_tokensTimely ? tokens.Where(x => x.CapturedAtUtc <= origin).ToArray() : tokens,
                    Workload = collected && !_workloadTimely ? workload.Where(x => x.CapturedAtUtc <= origin).ToArray() : workload,
                    Context = collected && !_contextTimely ? context.Where(x => x.CapturedAtUtc <= origin).ToArray() : context,
                };
            }
        }

    }
}
