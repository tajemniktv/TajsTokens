using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class QuotaCostObservationBuilder
{
    public const string Version = "quota-cost-observations/v3";
    public const string CoverageBoundary = "Construction counts eligible-epoch interior start candidates, not raw quota readings or independent outcomes. " +
        "Epoch endpoints are not candidates. Each rejected start has the first applicable reason in selection order; " +
        "candidate starts can overlap. No outcome in an epoch can mean a reset or range boundary, not missing collection. " +
        "Outcome polling tolerance is the smaller of five minutes and 20% of the requested horizon; actual elapsed time " +
        "is used for workload projection. Strict replay withholding is a later, separate stage and must not be subtracted from these counts.";

    public static IReadOnlyList<QuotaCostObservation> Build(CodexForecastDataset data,
        CancellationToken cancellationToken = default, bool userConfirmedRolloutOwnership = false,
        IReadOnlyList<double>? horizons = null) =>
        BuildDetailed(data, cancellationToken, userConfirmedRolloutOwnership, horizons).Observations;

    public static QuotaCostObservationBuild BuildDetailed(CodexForecastDataset data,
        CancellationToken cancellationToken = default, bool userConfirmedRolloutOwnership = false,
        IReadOnlyList<double>? horizons = null)
    {
        var result = new List<QuotaCostObservation>();
        var coverage = new List<QuotaCostConstructionCoverage>();
        var history = QuotaHistoryPolicy.Describe(data.Quota, data.CapturedAtUtc);
        foreach (var stream in QuotaHistoryPolicy.Streams(history))
        foreach (var epoch in QuotaForecastBacktester.SplitEpochs(QuotaHistoryPolicy.ReplayRows(stream)))
        foreach (var horizon in horizons ?? [0.5, 2d])
        {
            DateTimeOffset? previousEnd = null;
            var candidates = 0;
            var initialCount = result.Count;
            var rejected = new Dictionary<string, int>(StringComparer.Ordinal);
            void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
            for (var i = 1; i < epoch.Count - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = epoch[i];
                candidates++;
                if (start.CapturedAtUtc < previousEnd) { Reject("overlaps-selected-interval"); continue; }
                if (start.UsedPercent >= 100) { Reject("saturated-origin"); continue; }
                if (start.CapturedAtUtc - epoch[0].CapturedAtUtc < TimeSpan.FromMinutes(15)) { Reject("epoch-warmup"); continue; }
                var target = start.CapturedAtUtc.AddHours(horizon);
                var end = epoch.Skip(i + 1).FirstOrDefault(x => x.CapturedAtUtc >= target);
                if (end is null) { Reject("no-outcome-in-epoch"); continue; }
                if (end.CapturedAtUtc - target > TimeSpan.FromMinutes(Math.Min(5, horizon * 60 * .2))) { Reject("outcome-beyond-poll-tolerance"); continue; }
                // A -> B -> A metadata must not bridge the intervening regime merely because
                // GroupBy puts both A portions in one stream. Concurrent incompatible lanes
                // in the same meter identity are conservatively withheld as ambiguous.
                if (data.Quota.Any(x => x.CapturedAtUtc > start.CapturedAtUtc && x.CapturedAtUtc <= end.CapturedAtUtc &&
                    x.Provider == start.Provider && x.Profile == start.Profile && x.Source == start.Source &&
                    x.AccountKey == start.AccountKey && x.Kind == start.Kind && x.SessionId == start.SessionId &&
                    QuotaHistoryPolicy.Cohort(x) != stream.Key)) { Reject("intervening-incompatible-cohort"); continue; }
                // Saturation hides any further usage; it is not an ordinary bounded delta target.
                if (end.UsedPercent >= 100) { Reject("saturated-outcome"); continue; }
                previousEnd = end.CapturedAtUtc;
                var hours = (end.CapturedAtUtc - start.CapturedAtUtc).TotalHours;
                var features = CodexForecastFeatureBuilder.Build(data, end.CapturedAtUtc, hours);
                var tokens = data.Tokens.Where(x => x.ObservedAtUtc > start.CapturedAtUtc && x.ObservedAtUtc <= end.CapturedAtUtc).ToArray();
                double Sum(Func<CodexPredictiveTokenEvent, long> field) => tokens.Sum(x => (double)Math.Max(0, field(x)));
                double[] categories = [Sum(x => x.UncachedInputTokens), Sum(x => x.CacheReadTokens),
                    Sum(x => x.CacheWriteTokens), Sum(x => x.NonReasoningOutputTokens), Sum(x => x.ReasoningOutputTokens)];
                var runtime = data.Workload.Where(x => x.ObservedAtUtc > start.CapturedAtUtc &&
                    x.ObservedAtUtc <= end.CapturedAtUtc && x.EventType == "task_complete" && x.TimeToFirstTokenMilliseconds >= 0).ToArray();
                var flags = new List<string> { "local-co-observation-not-account-attribution", "retrospective-event-time" };
                if (stream.Key.AccountKey is null)
                    flags.Add(userConfirmedRolloutOwnership && start.Authority == QuotaObservationAuthority.EmbeddedObservation
                        ? "user-confirmed-rollout-ownership-native-account-id-absent" : "native-account-id-absent");
                if (tokens.Length == 0) flags.Add("no-recorded-tokens-not-proven-idle");
                if (tokens.Any(x => !QuotaEvaluationCoverageBuilder.HasCompleteCategories(x))) flags.Add("incomplete-token-categories");
                if (tokens.Any(x => x.Model is null || x.ReasoningEffort is null)) flags.Add("missing-model-or-effort");
                if (features.ObservedContextEvents == 0) flags.Add("missing-context");
                if (runtime.Length == 0) flags.Add("missing-runtime");
                if (tokens.Any(x => x.CapturedAtUtc is null || x.CapturedAtUtc > end.CapturedAtUtc)) flags.Add("backfilled-or-unknown-collection-time");
                // Upstream app-server integer conversion is corroborated, not a guarantee of
                // backend precision/lag. Fractional rollout representation establishes no step.
                // Its wider allowance is explicitly a sensitivity policy, never inferred precision.
                var rounded = start.Authority == QuotaObservationAuthority.ProviderAuthoritative &&
                    start.UsedPercent == Math.Truncate(start.UsedPercent!.Value) && end.UsedPercent == Math.Truncate(end.UsedPercent!.Value);
                var halfWidth = rounded ? 0.5 : 1;
                if (!rounded) flags.Add("meter-precision-unverified-sensitivity-only");
                var lower = Math.Max(0, Math.Max(0, end.UsedPercent!.Value - halfWidth) - Math.Min(100, start.UsedPercent!.Value + halfWidth));
                var upper = Math.Max(0, Math.Min(100, end.UsedPercent.Value + halfWidth) - Math.Max(0, start.UsedPercent.Value - halfWidth));
                var prefix = epoch.Take(i + 1).ToArray();
                var association = RolloutAccountAssociationPolicy.Resolve(start, data.AccountAssociations, data.CapturedAtUtc);
                if (association is not null && association.Id != RolloutAccountAssociationPolicy.Resolve(end, data.AccountAssociations, data.CapturedAtUtc)?.Id)
                    association = null;
                if (association is not null) flags.Add("user-asserted-account-association");
                // Availability includes every retained source family used by the cost features,
                // including old session metadata. Missing capture times are unknown, not event time.
                DateTimeOffset?[] tokenCollected = epoch.Take(i + 1).Append(end).Select(x => x.CollectedAtUtc)
                    .Concat(tokens.Select(x => x.CapturedAtUtc)).ToArray();
                if (association is not null) tokenCollected = tokenCollected.Append((DateTimeOffset?)association.AssertedAtUtc).ToArray();
                DateTimeOffset?[] collected = tokenCollected
                    .Concat(data.Workload.Where(x => x.ObservedAtUtc <= end.CapturedAtUtc).Select(x => (DateTimeOffset?)x.CapturedAtUtc))
                    .Concat(data.Context.Where(x => x.ObservedAtUtc > start.CapturedAtUtc && x.ObservedAtUtc <= end.CapturedAtUtc).Select(x => x.CapturedAtUtc)).ToArray();
                result.Add(new(stream.Key, epoch[0].CapturedAtUtc, epoch[0].ResetsAtUtc!.Value,
                    start.CapturedAtUtc, end.CapturedAtUtc, horizon, start.UsedPercent.Value, end.UsedPercent.Value,
                    lower, upper, rounded ? "app-server-rounding-envelope" : "unknown-precision-1pp-endpoint-sensitivity",
                    start.RemainingPercent!.Value - QuotaPaceModels.ProjectRemaining(prefix, "legacy-ewma", hours),
                    categories, features, runtime.Length > 0 ? runtime.Average(x => (double)x.TimeToFirstTokenMilliseconds!.Value) : null,
                    runtime.Length, flags)
                {
                    EvidenceAvailableAtUtc = collected.All(x => x is not null) ? collected.Max() : null,
                    TokenCostEvidenceAvailableAtUtc = tokenCollected.All(x => x is not null) ? tokenCollected.Max() : null,
                    ApiPriceWeight = ApiPriceWorkload.Calculate(tokens),
                    OriginCollectedAtUtc = start.CollectedAtUtc,
                    OriginEvidenceAvailableAtUtc = prefix.All(x => x.CollectedAtUtc is not null)
                        ? prefix.Max(x => x.CollectedAtUtc) : null,
                    OutcomeCollectedAtUtc = end.CollectedAtUtc,
                    EffectiveAccountKey = stream.Key.AccountKey ?? association?.AccountKey,
                    Attribution = stream.Key.AccountKey is not null ? QuotaAccountAttribution.ProviderVerified :
                        association is not null ? QuotaAccountAttribution.UserAsserted : QuotaAccountAttribution.Unattributed,
                    AccountAssociationId = association?.Id
                });
            }
            coverage.Add(new(stream.Key, horizon, candidates, result.Count - initialCount, rejected));
        }
        return new(result, coverage.GroupBy(x => (x.Cohort, x.HorizonHours)).Select(g =>
            new QuotaCostConstructionCoverage(g.Key.Cohort, g.Key.HorizonHours, g.Sum(x => x.CandidateStarts),
                g.Sum(x => x.BuiltIntervals), g.SelectMany(x => x.RejectedStarts).GroupBy(x => x.Key, StringComparer.Ordinal)
                    .OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(y => y.Value), StringComparer.Ordinal))).ToArray());
    }
}
