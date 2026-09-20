using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public static class CodexIntelligenceProjection
{
    public const string Version = "codex-intelligence/v1";

    public static CodexIntelligenceSnapshot Live(TelemetrySnapshot telemetry)
    {
        var at = telemetry.CapturedAtUtc;
        var selection = new CodexSelection(at, at.AddTicks(1));
        var current = telemetry.QuotaSnapshots.Select(q =>
        {
            var fresh = telemetry.IsQuotaSnapshotFresh(q);
            var matched = telemetry.FindCurrentForecast(q);
            return matched is not null ? matched with { Forecast = fresh && matched.IsFresh ? matched.Forecast : null } :
                new CurrentQuotaForecast(q, fresh ? Enums.TelemetryHealthState.Live : Enums.TelemetryHealthState.Stale,
                    null, "Reported meter retained while its forecast is unavailable.");
        }).ToArray();
        return new(at, selection, [], [], [], [], current, telemetry.TokenForecast, ApiPriceWorkload.Calculate([]), [],
            [new("Live collection", telemetry.QuotaDataFresh ? "Reported" : "Partial or stale", "History is loaded separately for the selected range.")],
            Manifest(selection, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { current, telemetry.TokenForecast }))))))
            { RuntimeEvidence = telemetry };
    }

    public static IReadOnlyList<CodexLedgerEntry> Select(IEnumerable<CodexLedgerEntry> ledger, CodexSelection selection) =>
        ledger.Where(x => x.ObservedAtUtc >= selection.FromUtc && x.ObservedAtUtc < selection.ToUtc &&
            (selection.AccountKey is null || x.AccountKey == selection.AccountKey) &&
            (selection.Model is null || x.Workload.Model == selection.Model) &&
            (selection.Project is null || x.Project == selection.Project) &&
            (selection.ThreadId is null || x.ThreadId == selection.ThreadId)).ToArray();

    public static CodexInferenceManifest Manifest(CodexSelection selection, string? evidenceIdentity = null)
    {
        var components = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["projection"] = Version, ["workload"] = TokenWorkloadPredictionService.PolicyVersion,
            ["accounting"] = QuotaCostEvaluation.Version, ["forecast"] = QuotaPredictionService.PolicyVersion,
            ["reset-outlook"] = QuotaPredictionService.ResetOutlookPolicyVersion,
            ["evidence"] = CodexEvidenceDrift.Policy, ["daily-pairing"] = CodexDailyPairing.Policy,
            ["api-reference"] = ApiPriceWorkload.Version,
            ["regime"] = CodexRegimeModel.Version,
            ["provider-reconciliation"] = CodexProviderReconciliation.Version,
            ["inputs-and-results"] = evidenceIdentity ?? "not-captured",
            ["tt"] = "research-only; no runtime basis selected"
        };
        var identity = JsonSerializer.Serialize(new { components, selection });
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), components,
            "Content identity of component versions, selection and captured inputs/results. A hash alone is not a fitted-model artifact: retained forecast outputs and accounting coefficients must accompany it. No retraining or historical restatement is implied.");
    }

    public static IReadOnlyList<CodexLedgerBucket> Buckets(IReadOnlyList<CodexLedgerEntry> rows, bool hourly) => rows
        .GroupBy(x => hourly
            ? new DateTimeOffset(x.ObservedAtUtc.UtcDateTime.Date.AddHours(x.ObservedAtUtc.UtcDateTime.Hour), TimeSpan.Zero)
            : new DateTimeOffset(x.ObservedAtUtc.UtcDateTime.Date, TimeSpan.Zero))
        .OrderBy(x => x.Key).Select(g => new CodexLedgerBucket(g.Key,
            g.Sum(x => x.Workload.ReportedTotalTokens), g.Sum(x => x.Workload.UncachedInputTokens),
            g.Sum(x => x.Workload.CacheReadTokens), g.Sum(x => x.Workload.CacheWriteTokens),
            g.Sum(x => x.Workload.NonReasoningOutputTokens), g.Sum(x => x.Workload.ReasoningOutputTokens))).ToArray();

    public static IReadOnlyList<CodexLedgerGroup> Groups(IReadOnlyList<CodexLedgerEntry> rows) =>
        GroupRows(rows, "Model", x => x.Workload.Model ?? "(unknown)")
            .Concat(GroupRows(rows, "Effort", x => x.Workload.ReasoningEffort ?? "(unknown)"))
            .Concat(GroupRows(rows, "Project", x => x.Project)).Concat(GroupRows(rows, "Chat", x => x.ThreadId))
            .Concat(new[] {
                new CodexLedgerGroup("Token type", "Uncached input", rows.Sum(x => x.Workload.UncachedInputTokens), rows.Select(x => x.ThreadId).Distinct().Count()),
                new CodexLedgerGroup("Token type", "Cache read", rows.Sum(x => x.Workload.CacheReadTokens), rows.Select(x => x.ThreadId).Distinct().Count()),
                new CodexLedgerGroup("Token type", "Cache write", rows.Sum(x => x.Workload.CacheWriteTokens), rows.Select(x => x.ThreadId).Distinct().Count()),
                new CodexLedgerGroup("Token type", "Output (non-reasoning)", rows.Sum(x => x.Workload.NonReasoningOutputTokens), rows.Select(x => x.ThreadId).Distinct().Count()),
                new CodexLedgerGroup("Token type", "Reasoning output", rows.Sum(x => x.Workload.ReasoningOutputTokens), rows.Select(x => x.ThreadId).Distinct().Count()) }).ToArray();

    private static IEnumerable<CodexLedgerGroup> GroupRows(IReadOnlyList<CodexLedgerEntry> rows, string dimension,
        Func<CodexLedgerEntry, string> key) => rows.GroupBy(key).OrderByDescending(g => g.Sum(x => x.Workload.ReportedTotalTokens))
        .Select(g => new CodexLedgerGroup(dimension, g.Key, g.Sum(x => x.Workload.ReportedTotalTokens), g.Select(x => x.ThreadId).Distinct().Count()));
}
