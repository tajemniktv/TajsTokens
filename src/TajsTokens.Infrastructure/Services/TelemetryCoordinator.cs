// Taj's Tokens | TelemetryCoordinator.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Collections.Concurrent;
using System.Diagnostics;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

#endregion

namespace TajsTokens.Infrastructure.Services;

/// <summary>
///     Owns provider refresh serialization for the whole process. Dashboard, tray, alerts, Observatory,
///     and historical intelligence consume one refresh cadence rather than creating competing loops.
/// </summary>
public sealed class TelemetryCoordinator
{
    private readonly object _activeRefreshSync = new();
    private readonly ConcurrentQueue<TelemetryRefreshEvent> _backgroundEvents = new();
    private readonly SemaphoreSlim _intelligenceRefreshGate = new(1, 1);
    private readonly IIntelligenceService? _intelligenceService;
    private readonly ICodexObservatoryService? _observatoryService;
    private readonly ICodexQuotaProvider _quotaProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SqliteTelemetryRepository _repository;
    private readonly CodexServerEvidenceService? _serverEvidence;
    private readonly ICodexTokenAccountingProvider _tokenProvider;
    private CancellationTokenSource? _activeRefreshCancellation;
    private RefreshTrigger? _activeRefreshTrigger;
    private string _lastTokenSourceName = "Token accounting";
    private TelemetrySnapshot _latest = TelemetrySnapshot.Empty;

    public TelemetryCoordinator(
        ICodexTokenAccountingProvider tokenProvider,
        ICodexQuotaProvider quotaProvider,
        SqliteTelemetryRepository repository,
        ICodexObservatoryService? observatoryService = null,
        IIntelligenceService? intelligenceService = null,
        CodexServerEvidenceService? serverEvidence = null)
    {
        _tokenProvider = tokenProvider;
        _quotaProvider = quotaProvider;
        _repository = repository;
        _observatoryService = observatoryService;
        _intelligenceService = intelligenceService;
        _serverEvidence = serverEvidence;
    }

    public TelemetrySnapshot Latest => Volatile.Read(ref _latest);

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>
    ///     Refreshes telemetry without ever running provider/database work on a caller's UI
    ///     SynchronizationContext. Microsoft.Data.Sqlite executes much of its work synchronously, so an
    ///     ordinary async call from WinUI can otherwise block the dispatcher despite using await.
    /// </summary>
    public Task<TelemetrySnapshot> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        return Task.Run(() => RefreshCoreAsync(trigger, cancellationToken), cancellationToken);
    }

    private async Task<TelemetrySnapshot> RefreshCoreAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        if (trigger == RefreshTrigger.Manual)
        {
            lock (_activeRefreshSync)
            {
                if (_activeRefreshTrigger is not null and not RefreshTrigger.Manual)
                {
                    _activeRefreshCancellation?.Cancel();
                }
            }
        }

        await _refreshGate.WaitAsync(cancellationToken);
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeRefreshSync)
        {
            _activeRefreshCancellation = refreshCancellation;
            _activeRefreshTrigger = trigger;
        }

        CancellationToken refreshToken = refreshCancellation.Token;
        try
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            TelemetrySnapshot previous = Latest;
            TokenWorkloadForecast? retainedTokenForecast = previous.TokenForecast is { } oldForecast
                ? oldForecast with { IsStale = true }
                : null;
            var sources = new List<ProviderHealthSnapshot>(6);
            var events = new List<TelemetryRefreshEvent>();
            while (_backgroundEvents.TryDequeue(out TelemetryRefreshEvent? backgroundEvent))
            {
                events.Add(backgroundEvent);
            }

            bool persistenceAvailable = false;
            try
            {
                await _repository.InitializeAsync(refreshToken);
                persistenceAvailable = true;
                sources.Add(
                    new ProviderHealthSnapshot(
                        "SQLite",
                        TelemetryHealthState.Live,
                        "Local telemetry history is available.",
                        startedAt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string detail = SummarizeError(exception);
                sources.Add(new ProviderHealthSnapshot("SQLite", TelemetryHealthState.Error, detail));
                events.Add(new TelemetryRefreshEvent(startedAt, "Persistence unavailable", detail));
            }

            // Token accounting begins with the last complete generation. The native projection is
            // intentionally refreshed only after the Observatory writer commits changed rollouts, so
            // the final snapshot contains the turn that triggered this refresh rather than lagging by
            // one cadence. Provider-authoritative quota can still publish progressively beforehand.
            IReadOnlyList<TokenUsage> tokenUsages = previous.TokenUsages;
            IReadOnlyList<TokenTimeBucket> hourlyBuckets = previous.HourlyBuckets;
            bool tokenFresh = false;
            TokenAccountingGenerationState? tokenGeneration = previous.TokenGeneration is null
                ? null
                : previous.TokenGeneration with { State = TelemetryHealthState.Stale };
            bool observatoryFresh = _observatoryService is null;
            CurrentQuotaForecast[] currentForecasts = previous.CurrentForecasts
                .Select(item => item with { State = TelemetryHealthState.Stale })
                .ToArray();

            IReadOnlyList<QuotaSnapshot> quotaSnapshots = previous.QuotaSnapshots;
            IReadOnlyList<QuotaLaneState> quotaLanes = BuildInitialQuotaLanes(previous);
            IReadOnlyList<QuotaSnapshot> freshQuotaSnapshots = [];
            CodexServerObservation? quotaMetadata = null;
            bool quotaFresh = false;
            bool quotaResponseHasSupportedWindow = false;
            try
            {
                CodexQuotaResponse response = await _quotaProvider.GetQuotaResponseAsync(refreshToken);
                freshQuotaSnapshots = response.Snapshots;
                if (freshQuotaSnapshots.Any(item => item.AccountKey != response.AccountKey))
                    throw new InvalidOperationException("Quota response contains inconsistent backend-account scope.");
                quotaMetadata = response.MetadataObservation;
                TelemetrySnapshot compatiblePrevious = previous with
                {
                    QuotaSnapshots = previous.QuotaSnapshots.Where(item => item.AccountKey == response.AccountKey).ToArray(),
                    QuotaLanes = previous.QuotaLanes.Select(lane =>
                        lane.Snapshot is { } retained && retained.AccountKey != response.AccountKey
                            ? lane with { Snapshot = null, State = TelemetryHealthState.Unavailable, LastSuccessUtc = null }
                            : lane).ToArray(),
                };
                currentForecasts = currentForecasts.Where(item => item.Current.AccountKey == response.AccountKey).ToArray();
                QuotaSnapshot[] supported = freshQuotaSnapshots
                    .Where(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
                    .ToArray();
                quotaResponseHasSupportedWindow = supported.Length > 0;
                quotaSnapshots = MergeQuotaSnapshots(compatiblePrevious.QuotaSnapshots, supported);

                quotaLanes = BuildQuotaLanes(compatiblePrevious, supported, startedAt);
                quotaFresh = quotaResponseHasSupportedWindow && quotaLanes.All(lane => lane.IsFresh || lane.NotReportedByProvider);
                int liveCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Live);
                int staleCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Stale);
                int unavailableCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Unavailable);

                TelemetryHealthState sourceState = quotaFresh
                    ? TelemetryHealthState.Live
                    : liveCount > 0 || staleCount > 0
                        ? TelemetryHealthState.Stale
                        : TelemetryHealthState.Unavailable;
                string detail = quotaFresh
                    ? $"{liveCount} reported quota window(s) refreshed; {quotaLanes.Count(lane => lane.NotReportedByProvider)} not reported by Codex. No model turn was created."
                    : quotaResponseHasSupportedWindow
                        ? $"Partial provider-authoritative quota refresh: {liveCount} live, {staleCount} stale, {unavailableCount} unavailable lane(s). Fresh lanes remain independently usable."
                        : "The app-server responded but did not expose a supported five-hour or weekly window; previous lanes remain stale when available.";

                detail += $" {QuotaAccountScope.Describe(response.AccountKey)}; local rollout work is not account-attributed.";
                sources.Add(
                    new ProviderHealthSnapshot(
                        "Codex app-server",
                        sourceState,
                        detail,
                        liveCount > 0 ? startedAt : PreviousSuccess(previous, "Codex app-server")));
                events.Add(
                    new TelemetryRefreshEvent(
                        startedAt,
                        "Quota refresh",
                        quotaFresh
                            ? "Refreshed the quota windows reported by Codex. Unreported windows are not treated as unlimited."
                            : quotaResponseHasSupportedWindow
                                ? "Captured a partial Codex quota response; retained omitted last-known-good lanes as stale."
                                : "Codex app-server returned no supported five-hour or weekly quota windows."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string detail = SummarizeError(exception);
                quotaLanes = BuildInitialQuotaLanes(previous);
                quotaFresh = false;
                bool hasPrevious = quotaLanes.Any(lane => lane.Snapshot is not null);
                sources.Add(
                    new ProviderHealthSnapshot(
                        "Codex app-server",
                        hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                        hasPrevious ? $"Using last-known-good quota lanes as stale. {detail}" : detail,
                        PreviousSuccess(previous, "Codex app-server")));
                events.Add(new TelemetryRefreshEvent(startedAt, "Quota unavailable", detail));
            }

            if (persistenceAvailable && (quotaResponseHasSupportedWindow || quotaMetadata is not null))
            {
                try
                {
                    if (quotaMetadata is not null)
                        await _repository.SaveServerEvidenceAsync(new CodexServerCollection([quotaMetadata]), refreshToken);
                    foreach (QuotaSnapshot snapshot in freshQuotaSnapshots.Where(snapshot =>
                                 snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly))
                    {
                        await _repository.UpsertQuotaSnapshotAsync(snapshot, refreshToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    persistenceAvailable = false;
                    string detail = SummarizeError(exception);
                    ReplaceSource(sources, new ProviderHealthSnapshot("SQLite", TelemetryHealthState.Error, detail));
                    events.Add(
                        new TelemetryRefreshEvent(
                            startedAt,
                            "Persistence error",
                            $"Live provider data is still available, but fresh quota observations were not stored: {detail}"));
                }
            }

            Task<CodexObservatoryRefreshResult>? observatoryTask = null;
            if (persistenceAvailable && _observatoryService is not null)
            {
                List<ProviderHealthSnapshot> scanSources = sources.ToList();
                scanSources.Add(
                    new ProviderHealthSnapshot(
                        "Codex rollouts",
                        TelemetryHealthState.Stale,
                        "Scanning local Codex rollout history (changed sources) in the background; provider quota is already usable and the last complete token generation remains visible.",
                        PreviousSuccess(previous, "Codex rollouts")));
                List<TelemetryRefreshEvent> scanEvents = events.ToList();
                scanEvents.Add(
                    new TelemetryRefreshEvent(
                        DateTimeOffset.UtcNow,
                        "Codex observatory scan",
                        "Incremental rollout ingestion started in the background. Native accounting will project the committed generation afterward."));

                PublishSnapshot(
                    trigger,
                    tokenUsages,
                    hourlyBuckets,
                    quotaSnapshots,
                    tokenFresh,
                    quotaFresh,
                    persistenceAvailable,
                    scanSources,
                    scanEvents,
                    quotaLanes,
                    tokenGeneration,
                    currentForecasts,
                    retainedTokenForecast);

                observatoryTask = _observatoryService.RefreshAsync(refreshToken);
            }

            if (observatoryTask is not null)
            {
                try
                {
                    CodexObservatoryRefreshResult observatory = await observatoryTask;
                    bool sourcePresent = observatory.FilesDiscovered > 0;
                    observatoryFresh = sourcePresent && observatory.Errors == 0 && observatory.DeferredFiles == 0;
                    TelemetryHealthState state = observatoryFresh
                        ? TelemetryHealthState.Live
                        : sourcePresent
                            ? TelemetryHealthState.Stale
                            : TelemetryHealthState.Unavailable;
                    string detail = observatory.FilesDiscovered == 0
                        ? "No local Codex rollout JSONL sources were discovered. Previously normalized history, if any, remains historical rather than live."
                        : $"{observatory.FilesDiscovered} catalog rollout(s), {observatory.FilesScanned} changed file(s) scanned, {observatory.RecordsScanned} new complete record(s), {observatory.RecordsNormalized} normalized, {observatory.SessionsTouched} touched session(s), {FormatByteCount(observatory.BytesObserved)} observed on changed sources." +
                          (observatory.Errors > 0
                              ? $" {observatory.Errors} file(s) could not be refreshed and will retry."
                              : string.Empty) +
                          (observatory.DeferredFiles > 0
                              ? $" {observatory.DeferredFiles} changed indexed file(s) remain queued for later bounded passes; token history is not yet fully refreshed."
                              : string.Empty);
                    if (observatory.Coverage is { } coverage)
                    {
                        detail += $" Best-effort path coverage at {coverage.ObservedAtUtc:u}: " +
                                  $"{coverage.AccessibleIndexedPaths}/{coverage.IndexedPaths} indexed paths accessible; " +
                                  $"{coverage.DiscoveredPaths} files in configured discovery roots, {coverage.UnindexedPaths} not indexed; " +
                                  $"{coverage.IndexedOutsideDiscovery} indexed paths outside that discovered set. " +
                                  "Unindexed files may be alternate copies, not missing tasks; they are not automatically imported while the state index is usable. " +
                                  "These path counts do not measure unique work or durable collection completeness. " +
                                  "Use Codex > Rollout coverage for bounded, read-only alternate-file comparisons.";
                    }
                    sources.Add(
                        new ProviderHealthSnapshot(
                            "Codex rollouts",
                            state,
                            detail,
                            observatoryFresh ? startedAt : PreviousSuccess(previous, "Codex rollouts")));
                    events.Add(new TelemetryRefreshEvent(startedAt, "Codex observatory", detail));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    observatoryFresh = false;
                    string detail = SummarizeError(exception);
                    sources.Add(
                        new ProviderHealthSnapshot(
                            "Codex rollouts",
                            TelemetryHealthState.Stale,
                            $"Rollout observatory refresh failed; previously normalized history remains available. {detail}",
                            PreviousSuccess(previous, "Codex rollouts")));
                    events.Add(new TelemetryRefreshEvent(startedAt, "Codex observatory unavailable", detail));
                }
            }

            refreshToken.ThrowIfCancellationRequested();

            try
            {
                CodexTokenAccountingSnapshot accounting = await _tokenProvider.GetSnapshotAsync(refreshToken);
                IReadOnlyList<TokenUsage> projectedUsages = accounting.Usage;
                IReadOnlyList<TokenTimeBucket> projectedHourly = accounting.Hourly;
                bool hasPreviousTokenGeneration = previous.TokenUsages.Count > 0 || previous.HourlyBuckets.Count > 0;
                bool accountingFresh = accounting.IsFallback || observatoryFresh;
                TelemetryDataQuality accountingQuality = accounting.IsFallback
                    ? TelemetryDataQuality.Fallback
                    : TelemetryDataQuality.Primary;

                if (accountingFresh)
                {
                    tokenUsages = projectedUsages;
                    hourlyBuckets = projectedHourly;
                    tokenFresh = true;
                    _lastTokenSourceName = accounting.Source;
                }
                else if (!hasPreviousTokenGeneration)
                {
                    tokenUsages = projectedUsages;
                    hourlyBuckets = projectedHourly;
                    tokenFresh = false;
                }

                TelemetryHealthState generationState = accountingFresh
                    ? TelemetryHealthState.Live
                    : tokenUsages.Count > 0 || hourlyBuckets.Count > 0
                        ? TelemetryHealthState.Stale
                        : TelemetryHealthState.Unavailable;

                if (accountingFresh || !hasPreviousTokenGeneration)
                {
                    tokenGeneration = new TokenAccountingGenerationState(
                        accounting.Source,
                        generationState,
                        accountingQuality,
                        accounting.Coverage,
                        accounting.AsOfUtc,
                        accounting.Revision,
                        accounting.Reconciliation,
                        accounting.Diagnostic);
                }
                else if (tokenGeneration is not null)
                {
                    tokenGeneration = tokenGeneration with
                    {
                        State = TelemetryHealthState.Stale,
                        Diagnostic = AppendDiagnostic(
                            tokenGeneration.Diagnostic,
                            "Upstream rollout ingestion was incomplete; preserving the last complete displayed token generation."),
                    };
                }

                string accountingDetail =
                    $"{projectedUsages.Count} model row(s), {projectedHourly.Count} hourly bucket(s). {accounting.Coverage}";
                if (accounting.IsFallback)
                {
                    accountingDetail += " Fresh fallback generation; fallback quality is degraded independently of freshness.";
                }
                else if (!observatoryFresh)
                {
                    accountingDetail += hasPreviousTokenGeneration
                        ? " Upstream rollout ingestion was incomplete; preserving the last complete displayed token generation."
                        : " Upstream rollout ingestion was incomplete; this first available projection is marked stale.";
                }
                if (!string.IsNullOrWhiteSpace(accounting.Diagnostic))
                {
                    accountingDetail += $" {accounting.Diagnostic}";
                }

                sources.Add(
                    new ProviderHealthSnapshot(
                        accounting.Source,
                        generationState,
                        accountingDetail,
                        generationState == TelemetryHealthState.Live
                            ? startedAt
                            : PreviousSuccess(previous, accounting.Source)));
                events.Add(
                    new TelemetryRefreshEvent(
                        startedAt,
                        accountingFresh ? "Token refresh" : "Token generation stale",
                        accountingFresh
                            ? $"Loaded {FormatTokenCount(projectedUsages.Sum(item => item.Breakdown.Total))} local-history tokens from {accounting.Source}" +
                              (accounting.IsFallback ? " using explicit fallback quality." : ".")
                            : "Native accounting projection remained usable, but upstream rollout ingestion was incomplete so the generation was not promoted as live."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                string detail = SummarizeError(exception);
                bool hasPrevious = previous.TokenUsages.Count > 0 || previous.HourlyBuckets.Count > 0;
                tokenUsages = previous.TokenUsages;
                hourlyBuckets = previous.HourlyBuckets;
                tokenFresh = false;
                if (tokenGeneration is not null)
                {
                    tokenGeneration = tokenGeneration with
                    {
                        State = TelemetryHealthState.Stale, Diagnostic = AppendDiagnostic(tokenGeneration.Diagnostic, detail),
                    };
                }
                sources.Add(
                    new ProviderHealthSnapshot(
                        _lastTokenSourceName,
                        hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                        hasPrevious ? $"Using last-known-good token data. {detail}" : detail,
                        PreviousSuccess(previous, _lastTokenSourceName)));
                events.Add(new TelemetryRefreshEvent(startedAt, "Token accounting unavailable", detail));
            }

            if (persistenceAvailable && _intelligenceService is not null)
            {
                try
                {
                    currentForecasts = (await _intelligenceService.BuildAndPersistCurrentForecastsAsync(
                        quotaLanes,
                        DateTimeOffset.UtcNow,
                        refreshToken)).ToArray();
                    int liveForecasts = currentForecasts.Count(item => item.IsFresh && item.Forecast is not null);
                    events.Add(
                        new TelemetryRefreshEvent(
                            DateTimeOffset.UtcNow,
                            "Current forecasts",
                            $"Built and persisted {liveForecasts} provider-anchored current forecast(s)."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    currentForecasts = currentForecasts
                        .Select(item => item with
                        {
                            State = TelemetryHealthState.Stale,
                            Diagnostic = AppendDiagnostic(item.Diagnostic, SummarizeError(exception)),
                        })
                        .ToArray();
                    events.Add(new TelemetryRefreshEvent(DateTimeOffset.UtcNow, "Forecast unavailable", SummarizeError(exception)));
                }
            }

            TokenWorkloadForecast? tokenForecast = retainedTokenForecast;
            if (persistenceAvailable && _intelligenceService is not null)
            {
                try
                {
                    tokenForecast = await _intelligenceService.ForecastTokenWorkloadAsync(DateTimeOffset.UtcNow, refreshToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    events.Add(new TelemetryRefreshEvent(DateTimeOffset.UtcNow, "Token forecast unavailable", SummarizeError(exception)));
                }
            }
            refreshToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            events.Add(
                new TelemetryRefreshEvent(
                    DateTimeOffset.UtcNow,
                    "Refresh completed",
                    $"{trigger.ToString().ToLowerInvariant()} refresh finished in {stopwatch.Elapsed.TotalSeconds:0.0}s."));

            TelemetrySnapshot publishedSnapshot = PublishSnapshot(
                trigger,
                tokenUsages,
                hourlyBuckets,
                quotaSnapshots,
                tokenFresh,
                quotaFresh,
                persistenceAvailable,
                sources,
                events,
                quotaLanes,
                tokenGeneration,
                currentForecasts,
                tokenForecast);

            // Historical intelligence is derived from already-persisted normalized telemetry. Queue
            // it only after the final telemetry snapshot has been published, and never hold the
            // shared provider/Observatory refresh gate while the heavier history scan is running.
            // A second request while one intelligence refresh is active is intentionally coalesced;
            // the next normal telemetry cadence will derive any newer persisted observations.
            if (persistenceAvailable && observatoryFresh && _intelligenceService is not null)
            {
                QueueIntelligenceRefresh(cancellationToken);
            }

            // Independent, throttled server evidence cannot delay or replace current quota anchors.
            // The service coalesces concurrent requests and owns its 30-minute backoff.
            if (persistenceAvailable && _serverEvidence is not null)
                _ = CollectServerEvidenceAsync(cancellationToken);

            return publishedSnapshot;
        }
        finally
        {
            lock (_activeRefreshSync)
            {
                if (ReferenceEquals(_activeRefreshCancellation, refreshCancellation))
                {
                    _activeRefreshCancellation = null;
                    _activeRefreshTrigger = null;
                }
            }

            _refreshGate.Release();
        }
    }

    private async Task CollectServerEvidenceAsync(CancellationToken token)
    {
        try
        {
            await _serverEvidence!.CollectAsync(false, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public async Task RunPeriodicAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (interval < TimeSpan.FromSeconds(15))
        {
            interval = TimeSpan.FromSeconds(15);
        }

        try
        {
            await RefreshAsync(RefreshTrigger.Startup, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            // A manual refresh intentionally supersedes an in-flight startup refresh.
        }
        catch (Exception exception)
        {
            _backgroundEvents.Enqueue(
                new TelemetryRefreshEvent(
                    DateTimeOffset.UtcNow,
                    "Startup refresh failed",
                    $"Periodic telemetry will retry. {SummarizeError(exception)}"));
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RefreshAsync(RefreshTrigger.Interval, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Manual refreshes intentionally supersede interval refreshes; this is not a failure.
                }
                catch (Exception exception)
                {
                    _backgroundEvents.Enqueue(
                        new TelemetryRefreshEvent(
                            DateTimeOffset.UtcNow,
                            "Periodic refresh failed",
                            $"Telemetry will retry on the next interval. {SummarizeError(exception)}"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void QueueIntelligenceRefresh(CancellationToken cancellationToken)
    {
        _ = RefreshIntelligenceAsync(cancellationToken);
    }

    private async Task RefreshIntelligenceAsync(CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            entered = await _intelligenceRefreshGate.WaitAsync(0, cancellationToken);
            if (!entered || _intelligenceService is null)
            {
                return;
            }

            IntelligenceRefreshResult intelligence = await _intelligenceService.RefreshAsync(cancellationToken);
            _backgroundEvents.Enqueue(
                new TelemetryRefreshEvent(
                    DateTimeOffset.UtcNow,
                    "Historical intelligence",
                    $"Detected {intelligence.ResetEventsDetected} new reset/re-anchor event(s); current forecasts are owned by the provider-anchored refresh path."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _backgroundEvents.Enqueue(
                new TelemetryRefreshEvent(
                    DateTimeOffset.UtcNow,
                    "Intelligence unavailable",
                    $"Telemetry remains available; historical intelligence will retry. {SummarizeError(exception)}"));
        }
        finally
        {
            if (entered)
            {
                _intelligenceRefreshGate.Release();
            }
        }
    }

    private TelemetrySnapshot PublishSnapshot(
        RefreshTrigger trigger,
        IReadOnlyList<TokenUsage> tokenUsages,
        IReadOnlyList<TokenTimeBucket> hourlyBuckets,
        IReadOnlyList<QuotaSnapshot> quotaSnapshots,
        bool tokenFresh,
        bool quotaFresh,
        bool persistenceAvailable,
        IEnumerable<ProviderHealthSnapshot> sources,
        IEnumerable<TelemetryRefreshEvent> events,
        IReadOnlyList<QuotaLaneState> quotaLanes,
        TokenAccountingGenerationState? tokenGeneration,
        IReadOnlyList<CurrentQuotaForecast> currentForecasts,
        TokenWorkloadForecast? tokenForecast = null)
    {
        var snapshot = new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            trigger,
            tokenUsages,
            hourlyBuckets,
            quotaSnapshots,
            tokenFresh,
            quotaFresh,
            persistenceAvailable,
            sources.ToArray(),
            events.ToArray())
        {
            QuotaLanes = quotaLanes, CurrentForecasts = currentForecasts, TokenGeneration = tokenGeneration, TokenForecast = tokenForecast,
        };

        Volatile.Write(ref _latest, snapshot);
        SnapshotUpdated?.Invoke(snapshot);
        return snapshot;
    }

    private static IReadOnlyList<QuotaLaneState> BuildInitialQuotaLanes(TelemetrySnapshot previous)
    {
        if (previous.QuotaLanes.Count > 0)
        {
            return previous.QuotaLanes
                .Select(lane => lane with
                {
                    State = lane.Snapshot is null ? TelemetryHealthState.Unavailable : TelemetryHealthState.Stale,
                    NotReportedByProvider = false,
                })
                .ToArray();
        }

        return ExpectedQuotaKeys()
            .Select(key =>
            {
                QuotaSnapshot? snapshot = previous.QuotaSnapshots
                    .Where(item => item.Kind == key.Kind &&
                                   string.Equals(item.Provider, key.Provider, StringComparison.Ordinal) &&
                                   string.Equals(item.Profile, key.Profile, StringComparison.Ordinal))
                    .OrderByDescending(item => item.CapturedAtUtc)
                    .FirstOrDefault();
                return new QuotaLaneState(
                    key.Kind,
                    key.Provider,
                    key.Profile,
                    snapshot,
                    snapshot is null ? TelemetryHealthState.Unavailable : TelemetryHealthState.Stale,
                    snapshot is null ? null : PreviousQuotaSuccess(previous, key.Kind, key.Provider, key.Profile));
            })
            .ToArray();
    }

    private static IReadOnlyList<QuotaLaneState> BuildQuotaLanes(
        TelemetrySnapshot previous,
        IReadOnlyList<QuotaSnapshot> fresh,
        DateTimeOffset observedAtUtc)
    {
        IReadOnlyList<QuotaLaneState> previousLanes = BuildInitialQuotaLanes(previous);
        (QuotaWindowKind Kind, string Provider, string Profile)[] keys = previousLanes
            .Select(lane => (lane.Kind, lane.Provider, lane.Profile))
            .Concat(fresh.Select(snapshot => (snapshot.Kind, snapshot.Provider, snapshot.Profile)))
            .Concat(ExpectedQuotaKeys())
            .Distinct()
            .ToArray();

        return keys.Select(key =>
        {
            QuotaSnapshot? freshSnapshot = fresh
                .Where(item => item.Kind == key.Kind &&
                               string.Equals(item.Provider, key.Provider, StringComparison.Ordinal) &&
                               string.Equals(item.Profile, key.Profile, StringComparison.Ordinal))
                .OrderByDescending(item => item.CapturedAtUtc)
                .FirstOrDefault();
            if (freshSnapshot is not null)
            {
                return new QuotaLaneState(
                    key.Kind,
                    key.Provider,
                    key.Profile,
                    freshSnapshot,
                    TelemetryHealthState.Live,
                    observedAtUtc);
            }

            QuotaLaneState? previousLane = previousLanes.FirstOrDefault(lane =>
                lane.Kind == key.Kind &&
                string.Equals(lane.Provider, key.Provider, StringComparison.Ordinal) &&
                string.Equals(lane.Profile, key.Profile, StringComparison.Ordinal));
            return previousLane is null
                ? new QuotaLaneState(
                    key.Kind,
                    key.Provider,
                    key.Profile,
                    null,
                    TelemetryHealthState.Unavailable,
                    NotReportedByProvider: fresh.Count > 0)
                : previousLane with
                {
                    State = previousLane.Snapshot is null
                        ? TelemetryHealthState.Unavailable
                        : TelemetryHealthState.Stale,
                    NotReportedByProvider = fresh.Count > 0,
                };
        }).ToArray();
    }

    private static IEnumerable<(QuotaWindowKind Kind, string Provider, string Profile)> ExpectedQuotaKeys()
    {
        yield return (QuotaWindowKind.FiveHour, "codex", "default");
        yield return (QuotaWindowKind.Weekly, "codex", "default");
    }

    private static DateTimeOffset? PreviousQuotaSuccess(
        TelemetrySnapshot snapshot,
        QuotaWindowKind kind,
        string provider,
        string profile)
    {
        return snapshot.QuotaLanes.FirstOrDefault(lane =>
            lane.Kind == kind &&
            string.Equals(lane.Provider, provider, StringComparison.Ordinal) &&
            string.Equals(lane.Profile, profile, StringComparison.Ordinal))?.LastSuccessUtc;
    }

    private static string AppendDiagnostic(string? existing, string addition)
    {
        return string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";
    }

    private static IReadOnlyList<QuotaSnapshot> MergeQuotaSnapshots(
        IReadOnlyList<QuotaSnapshot> previous,
        IReadOnlyList<QuotaSnapshot> fresh)
    {
        Dictionary<(string Provider, string Profile, QuotaWindowKind Kind), QuotaSnapshot> merged = previous.ToDictionary(
            snapshot => (snapshot.Provider, snapshot.Profile, snapshot.Kind),
            snapshot => snapshot);

        foreach (QuotaSnapshot snapshot in fresh)
        {
            merged[(snapshot.Provider, snapshot.Profile, snapshot.Kind)] = snapshot;
        }

        return merged.Values
            .OrderBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Provider, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Profile, StringComparer.Ordinal)
            .ToArray();
    }

    private static DateTimeOffset? PreviousSuccess(TelemetrySnapshot snapshot, string provider)
    {
        return snapshot.Sources.FirstOrDefault(source => string.Equals(source.Provider, provider, StringComparison.OrdinalIgnoreCase))
            ?.LastSuccessUtc;
    }

    private static void ReplaceSource(IList<ProviderHealthSnapshot> sources, ProviderHealthSnapshot replacement)
    {
        int index = sources.ToList().FindIndex(source => string.Equals(
            source.Provider,
            replacement.Provider,
            StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            sources[index] = replacement;
        }
        else
        {
            sources.Add(replacement);
        }
    }

    private static string FormatTokenCount(long value)
    {
        double absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0"),
        };
    }

    private static string FormatByteCount(long value)
    {
        double absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_073_741_824 => $"{value / 1_073_741_824d:0.00} GiB",
            >= 1_048_576 => $"{value / 1_048_576d:0.0} MiB",
            >= 1_024 => $"{value / 1_024d:0.0} KiB",
            _ => $"{value:N0} B",
        };
    }

    private static string SummarizeError(Exception exception)
    {
        string message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 320 ? message : message[..320] + "…";
    }
}