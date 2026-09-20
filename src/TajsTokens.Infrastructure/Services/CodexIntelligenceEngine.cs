// Taj's Tokens | CodexIntelligenceEngine.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Research;
using TajsTokens.Core.Services;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>Single product projection over existing evidence and runtime owners. Never starts a collector.</summary>
public sealed class CodexIntelligenceEngine(
    Func<ICodexForecastDatasetReader> datasetReader,
    ICodexServerEvidenceReader serverEvidence,
    IIntelligenceService intelligence,
    Func<TelemetrySnapshot> current) : ICodexIntelligence
{
    public CodexIntelligenceSnapshot Current => CodexIntelligenceProjection.Live(current());

    public Task<ScenarioEstimate> SimulateAsync(ScenarioRequest request, DateTimeOffset historyFromUtc, CancellationToken token)
    {
        return intelligence.EstimateScenarioAsync(request, historyFromUtc, token);
    }

    public Task<CodexIntelligenceSnapshot> QueryAsync(CodexSelection selection, CancellationToken token)
    {
        return Task.Run(() => ReadAsync(selection, token), token);
    }

    public Task<CodexIntelligenceSnapshot> AnalyzeAsync(CodexIntelligenceSnapshot snapshot, CancellationToken token)
    {
        return Task.Run(
            async () =>
            {
                if (snapshot.Selection.HasWorkFilter)
                    throw new InvalidOperationException(
                        "Clear model/project/chat filters for compatible account-wide accounting analysis; quota is not attributable to filtered work.");
                CodexForecastDataset data = await datasetReader().ReadAsync(
                    "codex",
                    "default",
                    snapshot.Selection.FromUtc,
                    snapshot.Selection.ToUtc,
                    token,
                    snapshot.Selection.AccountKey);
                QuotaCostReport cost = QuotaCostEvaluation.Evaluate(data, token);
                CodexRegimeReport regime = CodexRegimeModel.Analyze(cost, token);
                return snapshot with
                {
                    Accounting = cost,
                    Regime = regime,
                    Manifest = CodexIntelligenceProjection.Manifest(
                        snapshot.Selection,
                        Fingerprint(new { snapshot.Manifest.Id, cost, regime })),
                };
            },
            token);
    }

    private static string Fingerprint<T>(T value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    }

    private async Task<CodexIntelligenceSnapshot> ReadAsync(CodexSelection selection, CancellationToken token)
    {
        if (selection.FromUtc >= selection.ToUtc || selection.ToUtc - selection.FromUtc > TimeSpan.FromDays(366))
            throw new ArgumentException("Select an ordered range of at most 366 days.", nameof(selection));
        DateTimeOffset asOf = DateTimeOffset.UtcNow;
        CodexForecastDataset dataset = await datasetReader().ReadAsync(
            "codex",
            "default",
            selection.FromUtc,
            selection.ToUtc,
            token,
            selection.AccountKey,
            includeLedger: true);
        IReadOnlyList<CodexLedgerEntry> selected = CodexIntelligenceProjection.Select(dataset.Ledger, selection);
        CodexServerEvidenceHistory history = await serverEvidence.ReadHistoryAsync(selection.FromUtc, asOf, selection.AccountKey, token);
        IReadOnlyList<CodexServerObservation> evidence = history.Observations;
        bool evidenceTruncated = history.IsPartial;
        int invalidEvidence = history.InvalidCaptures;
        QuotaSnapshot[] quota = selection.HasWorkFilter
            ? []
            : dataset.Quota.Where(x =>
                x.CapturedAtUtc < selection.ToUtc && (selection.AccountKey is null || x.AccountKey == selection.AccountKey)).ToArray();
        CodexIntelligenceSnapshot live = Current;
        bool liveScope = !selection.HasWorkFilter && selection.FromUtc <= asOf && asOf - selection.ToUtc < TimeSpan.FromMinutes(5);
        CurrentQuotaForecast[] forecasts = liveScope
            ? live.Current.Where(x => selection.AccountKey is null || x.Current.AccountKey == selection.AccountKey).ToArray()
            : [];
        var health = new List<CodexEvidenceHealth>
        {
            new(
                "Local workload",
                selected.Count == 0 ? "Empty" : "Observed",
                $"{selected.Count:N0} canonical increments; {selected.Count(x => x.Ownership == AccountEvidenceClass.Unattributed):N0} unattributed, {selected.Count(x => x.Ownership == AccountEvidenceClass.UserDeclaredSingleAccount):N0} user-associated. Association is not native account identity."),
            new(
                "Quota",
                selection.HasWorkFilter ? "Scope unavailable" : "Reported",
                selection.HasWorkFilter
                    ? "Account quota cannot be attributed to a model, project or chat filter. It is withheld, not prorated by tokens."
                    : "Source/account lanes remain separate. Reset drops are not negative consumption; polls are not additive."),
            new(
                "Provider analytics",
                evidenceTruncated ? "Partial" : "Shadow",
                $"At most 1,000 captures / 16 MiB are inspected; {invalidEvidence} malformed captures unavailable. Reports may revise after the selected event range. They are not production calibration labels."),
            new(
                "Snapshot",
                "Multiple source captures",
                $"Local ledger and forecast dataset captured together at {dataset.CapturedAtUtc:O}; provider history upper bound {history.CapturedAtUtc:O}. Not an atomic cross-source snapshot."),
            new("TT", "Research only", "No runtime TT basis has been promoted. Missing TT is not zero workload."),
            new("API equivalent", "Derived", ApiPriceWorkload.Assumptions),
        };
        return new CodexIntelligenceSnapshot(
            asOf,
            selection,
            selected,
            CodexIntelligenceProjection.Buckets(selected, (selection.ToUtc - selection.FromUtc).TotalDays <= 2),
            CodexIntelligenceProjection.Groups(selected),
            quota,
            forecasts,
            liveScope && selection.AccountKey is null ? live.Workload : null,
            ApiPriceWorkload.Calculate(
                selected.Select(x => x.Workload).Select(x =>
                    (decimal)x.UncachedInputTokens + x.CacheReadTokens + x.CacheWriteTokens + x.NonReasoningOutputTokens +
                    x.ReasoningOutputTokens == x.ReportedTotalTokens
                        ? x
                        : x with { Model = null })),
            evidence.Where(x =>
                (selection.AccountKey is null || x.CorrelatedAccountKey == selection.AccountKey || x.CorrelatedAccountKey is null) &&
                (!selection.HasWorkFilter || selection.ThreadId is not null && x.ThreadId == selection.ThreadId &&
                    selection.Model is null && selection.Project is null)).ToArray(),
            health,
            CodexIntelligenceProjection.Manifest(
                selection,
                Fingerprint(new { selected, quota, forecasts, evidenceIds = evidence.Select(x => x.Id).ToArray() })))
        {
            CurrentState = live.CurrentState,
            Chats = CodexProviderReconciliation.Tasks(evidence, selected, selection),
            HistoricalPeriods = selection.HasWorkFilter
                ? []
                : CodexProviderReconciliation.Analyze(evidence, selected, quota, selection, asOf),
        };
    }
}