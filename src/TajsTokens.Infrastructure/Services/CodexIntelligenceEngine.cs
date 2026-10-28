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
    Func<TelemetrySnapshot> current,
    Func<CancellationToken, Task<CodexModelCatalogHistory>>? modelCatalogHistory = null,
    Func<CancellationToken, Task<WorkloadReferenceState>>? workloadReferences = null,
    Func<WorkloadReferenceState?>? currentReferences = null) : ICodexIntelligence
{
    private readonly object _statusGate = new();
    private IReadOnlyList<TokenUsage>? _statusTokens;
    private string? _statusBasis;
    private string? _statusSource;
    private CodexMeasuredWork? _retainedWork;
    private CodexProductStatus? _previousStatus;
    private IReadOnlyList<CodexStatusChange> _lastChanges = [];

    public CodexIntelligenceSnapshot Current
    {
        get
        {
            var telemetry = current();
            var references = currentReferences?.Invoke();
            var now = DateTimeOffset.UtcNow;
            var live = CodexIntelligenceProjection.Live(telemetry);
            lock (_statusGate)
            {
                string? basis = references?.Selection?.Basis.BasisId;
                string? source = telemetry.TokenGeneration?.Source + "/" + telemetry.TokenGeneration?.Quality;
                if (_retainedWork is null || !ReferenceEquals(_statusTokens, telemetry.TokenUsages) || basis != _statusBasis || source != _statusSource)
                {
                    _retainedWork = CodexProductProjection.RetainedWork(telemetry, references);
                    _statusTokens = telemetry.TokenUsages; _statusBasis = basis; _statusSource = source;
                }
                live = live with { RetainedWork = CodexProductProjection.WithFreshness(_retainedWork, telemetry, now) };
                if (references is not null)
                {
                    var reference = new WorkloadReferenceResult(references, live.RetainedWork.Tt, null, null);
                    live = live with { Reference = reference,
                        Manifest = CodexIntelligenceProjection.Manifest(live.Selection, live.Manifest.Id, reference) };
                }
                var product = CodexProductProjection.Build(live, now);
                var changes = CodexProductProjection.Changes(_previousStatus, product);
                if (changes.Count > 0) _lastChanges = changes;
                _previousStatus = product;
                return live with { StatusChanges = _lastChanges };
            }
        }
    }

    public Task<RecentScenarioPattern> GetRecentScenarioPatternAsync(DateTimeOffset nowUtc, CancellationToken token)
        => Task.Run(() => intelligence.GetRecentScenarioPatternAsync(nowUtc, token), token);

    public Task<RecentWorkPlan> PlanRecentWorkAsync(RecentWorkPlanRequest request, CancellationToken token)
    {
        RecentWorkPlanBuilder.Validate(request);
        return Task.Run(async () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CodexIntelligenceSnapshot live = Current;
            // Account selection scopes quota, never installation-local workload. No page/history filters enter here.
            var data = await datasetReader().ReadLedgerAndQuotaAsync("codex", "default", now.AddHours(-request.ContextHours),
                now.AddTicks(1), token, includeQuota: false);
            data = data with { Tokens = data.Ledger.Select(x => x.Workload).ToArray() };
            WorkloadReferenceState references;
            try { references = workloadReferences is null ? new(null, null) : await workloadReferences(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException) { references = new(null, null, Issue: "reference-selection-unavailable"); }
            token.ThrowIfCancellationRequested();
            return RecentWorkPlanBuilder.Build(data, live, request, references, now);
        }, token);
    }

    public Task<ScenarioEstimate> SimulateAsync(ScenarioRequest request, DateTimeOffset historyFromUtc, CancellationToken token)
    {
        CodexIntelligenceSnapshot live = Current;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        QuotaHistoryCohort[] cohorts = live.Current.Where(x => x.IsFresh &&
                live.CurrentState.IsQuotaSnapshotFresh(x.Current) && x.Current.AccountKey == request.AccountKey &&
                x.Current.Authority == QuotaObservationAuthority.ProviderAuthoritative &&
                x.Current.CapturedAtUtc <= now && now - x.Current.CapturedAtUtc <= TimeSpan.FromHours(6) &&
                x.Current.ResetsAtUtc > now && live.CurrentState.FindQuotaLane(x.Current)?.NotReportedByProvider != true)
            .Select(x => QuotaHistoryPolicy.Cohort(x.Current)).Distinct().ToArray();
        if (string.IsNullOrWhiteSpace(request.AccountKey) || cohorts.Length == 0)
            throw new InvalidOperationException("A fresh provider meter with a known account and future reset is required for planning.");
        return Task.Run(() => intelligence.EstimateScenarioAsync(request with { Cohorts = cohorts }, historyFromUtc, token), token);
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
                        Fingerprint(new { snapshot.Manifest.Id, cost, regime }), snapshot.Reference),
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
        CodexForecastDataset dataset = await datasetReader().ReadLedgerAndQuotaAsync(
            "codex", "default", selection.FromUtc, selection.ToUtc, token,
            selection.AccountKey, includeQuota: !selection.HasWorkFilter);
        IReadOnlyList<CodexLedgerEntry> selected = CodexIntelligenceProjection.Select(dataset.Ledger, selection);
        var joint = CodexJointWorkloadBuilder.Build(selected.Select(x => x.Workload));
        WorkloadReferenceState references;
        try { references = workloadReferences is null ? new(null, null) : await workloadReferences(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        { references = new(null, null, Issue: "reference-selection-unavailable"); }
        var reference = WorkloadReferenceProjection.Build(references, joint, selected, asOf);
        CodexModelCatalogHistory catalogs;
        try { catalogs = modelCatalogHistory is null ? CodexModelCatalogHistory.Empty : await modelCatalogHistory(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            catalogs = new(new(CodexModelCatalogReadState.Unavailable, asOf, asOf, null, null, "catalog-history-unavailable"), [], true, 1);
        }
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
        CurrentQuotaForecast[] forecasts = live.Current.Where(x => selection.AccountKey is null || x.Current.AccountKey == selection.AccountKey).ToArray();
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
                    ? "Live account allowances remain visible separately. Filtered quota history is withheld, never prorated by tokens."
                    : "Source/account lanes remain separate. Reset drops are not negative consumption; polls are not additive."),
            new(
                "Provider analytics",
                evidenceTruncated ? "Partial" : "Shadow",
                $"At most 1,000 captures / 16 MiB are inspected; {invalidEvidence} malformed captures unavailable. Reports may revise after the selected event range. They are not production calibration labels."),
            new(
                "Snapshot",
                "Multiple source captures",
                $"Local ledger and quota history captured together at {dataset.CapturedAtUtc:O}; provider history upper bound {history.CapturedAtUtc:O}. Not an atomic cross-source snapshot."),
            new("TT", reference.Tt?.State.ToString() ?? "Unavailable", reference.BasisLabel + ". Frozen reference consumption, not remaining allowance."),
            new("API equivalent", reference.CurrentPrice?.State.ToString() ?? "Unavailable", "Selected snapshot-reference valuation; actual billing conditions and effective dates are not inferred."),
        };
        return CodexIntelligenceProjection.WithLiveContext(new CodexIntelligenceSnapshot(
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
                Fingerprint(new { selected, quota, forecasts, evidenceIds = evidence.Select(x => x.Id).ToArray(),
                    catalogs.LastRead, reference, modelCatalogs = catalogs.Versions.Select(x => new { x.Definition.DefinitionId, x.LastCollectedAtUtc, x.LastSourceFetchedAtUtc }) }), reference))
        {
            CurrentState = live.CurrentState,
            JointWorkload = joint,
            Reference = reference,
            DatasetCapturedAtUtc = dataset.CapturedAtUtc,
            ModelCatalog = CodexModelCatalogProjection.Build(catalogs, selected),
            Chats = CodexProviderReconciliation.Tasks(evidence, selected, selection),
            HistoricalPeriods = selection.HasWorkFilter
                ? []
                : CodexProviderReconciliation.Analyze(evidence, selected, quota, selection, asOf),
        }, live, selection.AccountKey);
    }
}
