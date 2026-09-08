using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>Non-overlapping authoritative intervals with workload features available at interval start.</summary>
public static class CodexScenarioHistoryBuilder
{
    public static IReadOnlyList<ScenarioHistorySample> Build(CodexForecastDataset data)
    {
        var result = new List<ScenarioHistorySample>();
        foreach (var stream in data.Quota.Where(x => x.Authority == QuotaObservationAuthority.ProviderAuthoritative)
                     .GroupBy(x => (x.Provider, x.Profile, x.Kind, x.Source)))
        foreach (var epoch in QuotaForecastBacktester.SplitEpochs(stream))
        {
            var start = 0;
            for (var end = 1; end < epoch.Count; end++)
            {
                var before = epoch[start];
                var after = epoch[end];
                var hours = (after.CapturedAtUtc - before.CapturedAtUtc).TotalHours;
                if (hours < 0.5) continue;
                start = end;
                if (hours > 2 || before.UsedPercent >= 100) continue;
                var features = CodexForecastFeatureBuilder.Build(data, before.CapturedAtUtc);
                // Missing local evidence is not an observed zero-agent workload.
                if (features.ObservedTokenEvents == 0 || features.TokenActiveUnknownSessions > 0) continue;
                string? Exclusive(IReadOnlyDictionary<string, double> shares) =>
                    shares.Count == 1 && shares.First().Value >= 0.999 ? shares.First().Key : null;
                result.Add(new ScenarioHistorySample(before.Kind, before.CapturedAtUtc, after.CapturedAtUtc,
                    after.UsedPercent!.Value - before.UsedPercent!.Value,
                    features.TokenActiveRootSessions, features.TokenActiveSubagentSessions,
                    Exclusive(features.ModelTokenShares), Exclusive(features.EffortTokenShares), before.ResetsAtUtc, before.Source));
            }
        }
        return result;
    }
}
