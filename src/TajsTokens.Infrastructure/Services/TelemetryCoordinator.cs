using System.Collections.Concurrent;
using System.Diagnostics;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Owns provider refresh serialization for the whole process. Dashboard, tray, alerts, Observatory,
/// and historical intelligence consume one refresh cadence rather than creating competing loops.
/// </summary>
public sealed class TelemetryCoordinator
{
    private readonly ICodexTokenAccountingProvider _tokenProvider;
    private readonly ICodexQuotaProvider _quotaProvider;
    private readonly SqliteTelemetryRepository _repository;
    private readonly ICodexObservatoryService? _observatoryService;
    private readonly IIntelligenceService? _intelligenceService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _intelligenceRefreshGate = new(1, 1);
    private readonly ConcurrentQueue<TelemetryRefreshEvent> _backgroundEvents = new();
    private readonly object _activeRefreshSync = new();
    private CancellationTokenSource? _activeRefreshCancellation;
    private RefreshTrigger? _activeRefreshTrigger;
    private TelemetrySnapshot _latest = TelemetrySnapshot.Empty;
    private string _lastTokenSourceName = "Token accounting";

    public TelemetryCoordinator(
        ICodexTokenAccountingProvider tokenProvider,
        ICodexQuotaProvider quotaProvider,
        SqliteTelemetryRepository repository,
        ICodexObservatoryService? observatoryService = null,
        IIntelligenceService? intelligenceService = null)
    {
        _tokenProvider = tokenProvider;
        _quotaProvider = quotaProvider;
        _repository = repository;
        _observatoryService = observatoryService;
        _intelligenceService = intelligenceService;
    }

    public TelemetrySnapshot Latest => Volatile.Read(ref _latest);

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>
    /// Refreshes telemetry without ever running provider/database work on a caller's UI
    /// SynchronizationContext. Microsoft.Data.Sqlite executes much of its work synchronously, so an
    /// ordinary async call from WinUI can otherwise block the dispatcher despite using await.
    /// </summary>
    public Task<TelemetrySnapshot> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken) =>
        Task.Run(() => RefreshCoreAsync(trigger, cancellationToken), cancellationToken);

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

        var refreshToken = refreshCancellation.Token;
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var previous = Latest;
            var sources = new List<ProviderHealthSnapshot>(6);
            var events = new List<TelemetryRefreshEvent>();
            while (_backgroundEvents.TryDequeue(out var backgroundEvent))
            {
                events.Add(backgroundEvent);
            }

            var persistenceAvailable = false;
            try
            {
                await _repository.InitializeAsync(refreshToken);
                persistenceAvailable = true;
                sources.Add(new ProviderHealthSnapshot(
                    "SQLite",
                    TelemetryHealthState.Live,
                    "Local telemetry history is available.",
                    startedAt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var detail = SummarizeError(exception);
                sources.Add(new ProviderHealthSnapshot("SQLite", TelemetryHealthState.Error, detail));
                events.Add(new TelemetryRefreshEvent(startedAt, "Persistence unavailable", detail));
            }

            // Token accounting begins with the last complete generation. The native projection is
            // intentionally refreshed only after the Observatory writer commits changed rollouts, so
            // the final snapshot contains the turn that triggered this refresh rather than lagging by
            // one cadence. Provider-authoritative quota can still publish progressively beforehand.
            var tokenUsages = previous.TokenUsages;
            var hourlyBuckets = previous.HourlyBuckets;
            var tokenFresh = false;
            var tokenGeneration = previous.TokenGeneration is null
                ? null
                : previous.TokenGeneration with { State = TelemetryHealthState.Stale };
            var observatoryFresh = _observatoryService is null;
            var currentForecasts = previous.CurrentForecasts
                .Select(item => item with { State = TelemetryHealthState.Stale })
                .ToArray();

            var quotaSnapshots = previous.QuotaSnapshots;
            var quotaLanes = BuildInitialQuotaLanes(previous);
            IReadOnlyList<QuotaSnapshot> freshQuotaSnapshots = [];
            var quotaFresh = false;
            var quotaResponseHasSupportedWindow = false;
            try
            {
                freshQuotaSnapshots = await _quotaProvider.GetQuotaSnapshotsAsync(refreshToken);
                var supported = freshQuotaSnapshots
                    .Where(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
                    .ToArray();
                quotaResponseHasSupportedWindow = supported.Length > 0;
                if (quotaResponseHasSupportedWindow)
                {
                    quotaSnapshots = MergeQuotaSnapshots(previous.QuotaSnapshots, supported);
                }

                quotaLanes = BuildQuotaLanes(previous, supported, startedAt);
                quotaFresh = quotaLanes.Count > 0 && quotaLanes.All(lane => lane.IsFresh);
                var liveCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Live);
                var staleCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Stale);
                var unavailableCount = quotaLanes.Count(lane => lane.State == TelemetryHealthState.Unavailable);

                var sourceState = quotaFresh
                    ? TelemetryHealthState.Live
                    : liveCount > 0 || staleCount > 0
                        ? TelemetryHealthState.Stale
                        : TelemetryHealthState.Unavailable;
                var detail = quotaFresh
                    ? $"{liveCount} supported provider-authoritative quota lane(s) refreshed. No model turn was created."
                    : quotaResponseHasSupportedWindow
                        ? $"Partial provider-authoritative quota refresh: {liveCount} live, {staleCount} stale, {unavailableCount} unavailable lane(s). Fresh lanes remain independently usable."
                        : "The app-server responded but did not expose a supported five-hour or weekly window; previous lanes remain stale when available.";

                sources.Add(new ProviderHealthSnapshot(
                    "Codex app-server",
                    sourceState,
                    detail,
                    liveCount > 0 ? startedAt : PreviousSuccess(previous, "Codex app-server")));
                events.Add(new TelemetryRefreshEvent(
                    startedAt,
                    "Quota refresh",
                    quotaFresh
                        ? "Captured a complete provider-authoritative Codex quota snapshot."
                        : quotaResponseHasSupportedWindow
                            ? "Captured a partial Codex quota response; retained omitted last-known-good lanes as stale."
                            : "Codex app-server returned no supported five-hour or weekly quota windows."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var detail = SummarizeError(exception);
                quotaLanes = BuildInitialQuotaLanes(previous);
                quotaFresh = false;
                var hasPrevious = quotaLanes.Any(lane => lane.Snapshot is not null);
                sources.Add(new ProviderHealthSnapshot(
                    "Codex app-server",
                    hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                    hasPrevious ? $"Using last-known-good quota lanes as stale. {detail}" : detail,
                    PreviousSuccess(previous, "Codex app-server")));
                events.Add(new TelemetryRefreshEvent(startedAt, "Quota unavailable", detail));
            }

            if (persistenceAvailable && quotaResponseHasSupportedWindow)
            {
                try
                {
                    foreach (var snapshot in freshQuotaSnapshots.Where(snapshot =>
                                 snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly))
                    {
                        await _repository.UpsertQuotaSnapshotAsync(snapshot, refreshToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    persistenceAvailable = false;
                    var detail = SummarizeError(exception);
                    ReplaceSource(sources, new ProviderHealthSnapshot("SQLite", TelemetryHealthState.Error, detail));
                    events.Add(new TelemetryRefreshEvent(
                        startedAt,
                        "Persistence error",
                        $"Live provider data is still available, but fresh quota observations were not stored: {detail}"));
                }
            }

            Task<CodexObservatoryRefreshResult>? observatoryTask = null;
            if (persistenceAvailable && _observatoryService is not null)
            {
                var scanSources = sources.ToList();
                scanSources.Add(new ProviderHealthSnapshot(
                    "Codex rollouts",
                    TelemetryHealthState.Stale,
                    "Scanning local Codex rollout history (changed sources) in the background; provider quota is already usable and the last complete token generation remains visible.",
                    PreviousSuccess(previous, "Codex rollouts")));
                var scanEvents = events.ToList();
                scanEvents.Add(new TelemetryRefreshEvent(
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
                    currentForecasts);

                observatoryTask = _observatoryService.RefreshAsync(refreshToken);
            }

            if (observatoryTask is not null)
            {
                try
                {
                    var observatory = await observatoryTask;
                    var sourcePresent = observatory.FilesDiscovered > 0;
                    observatoryFresh = sourcePresent && observatory.Errors == 0;
                    var state = observatoryFresh
                        ? TelemetryHealthState.Live
                        : sourcePresent
                            ? TelemetryHealthState.Stale
                            : TelemetryHealthState.Unavailable;
                    var detail = observatory.FilesDiscovered == 0
                        ? "No local Codex rollout JSONL sources were discovered. Previously normalized history, if any, remains historical rather than live."
                        : $"{observatory.FilesDiscovered} catalog rollout(s), {observatory.FilesScanned} changed file(s) scanned, {observatory.RecordsScanned} new complete record(s), {observatory.RecordsNormalized} normalized, {observatory.SessionsTouched} touched session(s), {FormatByteCount(observatory.BytesObserved)} observed on changed sources." +
                          (observatory.Errors > 0 ? $" {observatory.Errors} file(s) could not be refreshed and will retry." : string.Empty);
                    sources.Add(new ProviderHealthSnapshot(
                        "Codex rollouts",
                        state,
                        detail,
                        observatoryFresh ? startedAt : PreviousSuccess(previous, "Codex rollouts")));
                    events.Add(new TelemetryRefreshEvent(startedAt, "Codex observatory", detail));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    observatoryFresh = false;
                    var detail = SummarizeError(exception);
                    sources.Add(new ProviderHealthSnapshot(
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
                var accounting = await _tokenProvider.GetSnapshotAsync(refreshToken);
                var projectedUsages = accounting.Usage;
                var projectedHourly = accounting.Hourly;
                var hasPreviousTokenGeneration = previous.TokenUsages.Count > 0 || previous.HourlyBuckets.Count > 0;
                var accountingFresh = accounting.IsFallback || observatoryFresh;
                var accountingQuality = accounting.IsFallback
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

                var generationState = accountingFresh
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
                            "Upstream rollout ingestion was incomplete; preserving the last complete displayed token generation.")
                    };
                }

                var accountingDetail = $"{projectedUsages.Count} model row(s), {projectedHourly.Count} hourly bucket(s). {accounting.Coverage}";
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

                sources.Add(new ProviderHealthSnapshot(
                    accounting.Source,
                    generationState,
                    accountingDetail,
                    generationState == TelemetryHealthState.Live
                        ? startedAt
                        : PreviousSuccess(previous, accounting.Source)));
                events.Add(new TelemetryRefreshEvent(
                    startedAt,
                    accountingFresh ? "Token refresh" : "Token generation stale",
                    accountingFresh
                        ? $"Loaded {FormatTokenCount(projectedUsages.Sum(item => item.Breakdown.Total))} local-history tokens from {accounting.Source}" +
                          (accounting.IsFallback ? " using explicit fallback quality." : ".")
                        : "Native accounting projection remained usable, but upstream rollout ingestion was incomplete so the generation was not promoted as live."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var detail = SummarizeError(exception);
                var hasPrevious = previous.TokenUsages.Count > 0 || previous.HourlyBuckets.Count > 0;
                tokenUsages = previous.TokenUsages;
                hourlyBuckets = previous.HourlyBuckets;
                tokenFresh = false;
                if (tokenGeneration is not null)
                {
                    tokenGeneration = tokenGeneration with
                    {
                        State = TelemetryHealthState.Stale,
                        Diagnostic = AppendDiagnostic(tokenGeneration.Diagnostic, detail)
                    };
                }
                sources.Add(new ProviderHealthSnapshot(
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
                        quotaLanes, DateTimeOffset.UtcNow, refreshToken)).ToArray();
                    var liveForecasts = currentForecasts.Count(item => item.IsFresh && item.Forecast is not null);
                    events.Add(new TelemetryRefreshEvent(
                        DateTimeOffset.UtcNow,
                        "Current forecasts",
                        $"Built and persisted {liveForecasts} provider-anchored current forecast(s)."));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    currentForecasts = previous.CurrentForecasts
                        .Select(item => item with { State = TelemetryHealthState.Stale, Diagnostic = AppendDiagnostic(item.Diagnostic, SummarizeError(exception)) })
                        .ToArray();
                    events.Add(new TelemetryRefreshEvent(DateTimeOffset.UtcNow, "Forecast unavailable", SummarizeError(exception)));
                }
            }

            refreshToken.ThrowIfCancellationRequested();
            stopwatch.Stop();
            events.Add(new TelemetryRefreshEvent(
                DateTimeOffset.UtcNow,
                "Refresh completed",
                $"{trigger.ToString().ToLowerInvariant()} refresh finished in {stopwatch.Elapsed.TotalSeconds:0.0}s."));

            var publishedSnapshot = PublishSnapshot(
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
                currentForecasts);

            // Historical intelligence is derived from already-persisted normalized telemetry. Queue
            // it only after the final telemetry snapshot has been published, and never hold the
            // shared provider/Observatory refresh gate while the heavier history scan is running.
            // A second request while one intelligence refresh is active is intentionally coalesced;
            // the next normal telemetry cadence will derive any newer persisted observations.
            if (persistenceAvailable && observatoryFresh && _intelligenceService is not null)
            {
                QueueIntelligenceRefresh(cancellationToken);
            }

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
            _backgroundEvents.Enqueue(new TelemetryRefreshEvent(
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
                    _backgroundEvents.Enqueue(new TelemetryRefreshEvent(
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
        var entered = false;
        try
        {
            entered = await _intelligenceRefreshGate.WaitAsync(0, cancellationToken);
            if (!entered || _intelligenceService is null)
            {
                return;
            }

            var intelligence = await _intelligenceService.RefreshAsync(cancellationToken);
            _backgroundEvents.Enqueue(new TelemetryRefreshEvent(
                DateTimeOffset.UtcNow,
                "Historical intelligence",
                $"Detected {intelligence.ResetEventsDetected} new reset/re-anchor event(s); current forecasts are owned by the provider-anchored refresh path."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _backgroundEvents.Enqueue(new TelemetryRefreshEvent(
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
        IReadOnlyList<CurrentQuotaForecast> currentForecasts)
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
            QuotaLanes = quotaLanes,
            CurrentForecasts = currentForecasts,
            TokenGeneration = tokenGeneration
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
                    State = lane.Snapshot is null ? TelemetryHealthState.Unavailable : TelemetryHealthState.Stale
                })
                .ToArray();
        }

        return ExpectedQuotaKeys()
            .Select(key =>
            {
                var snapshot = previous.QuotaSnapshots
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
        var previousLanes = BuildInitialQuotaLanes(previous);
        var keys = previousLanes
            .Select(lane => (lane.Kind, lane.Provider, lane.Profile))
            .Concat(fresh.Select(snapshot => (snapshot.Kind, snapshot.Provider, snapshot.Profile)))
            .Concat(ExpectedQuotaKeys())
            .Distinct()
            .ToArray();

        return keys.Select(key =>
        {
            var freshSnapshot = fresh
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

            var previousLane = previousLanes.FirstOrDefault(lane =>
                lane.Kind == key.Kind &&
                string.Equals(lane.Provider, key.Provider, StringComparison.Ordinal) &&
                string.Equals(lane.Profile, key.Profile, StringComparison.Ordinal));
            return previousLane is null
                ? new QuotaLaneState(key.Kind, key.Provider, key.Profile, null, TelemetryHealthState.Unavailable)
                : previousLane with
                {
                    State = previousLane.Snapshot is null
                        ? TelemetryHealthState.Unavailable
                        : TelemetryHealthState.Stale
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
        string profile) =>
        snapshot.QuotaLanes.FirstOrDefault(lane =>
            lane.Kind == kind &&
            string.Equals(lane.Provider, provider, StringComparison.Ordinal) &&
            string.Equals(lane.Profile, profile, StringComparison.Ordinal))?.LastSuccessUtc;

    private static string AppendDiagnostic(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

    private static IReadOnlyList<QuotaSnapshot> MergeQuotaSnapshots(
        IReadOnlyList<QuotaSnapshot> previous,
        IReadOnlyList<QuotaSnapshot> fresh)
    {
        var merged = previous.ToDictionary(
            snapshot => (snapshot.Provider, snapshot.Profile, snapshot.Kind),
            snapshot => snapshot);

        foreach (var snapshot in fresh)
        {
            merged[(snapshot.Provider, snapshot.Profile, snapshot.Kind)] = snapshot;
        }

        return merged.Values
            .OrderBy(snapshot => snapshot.Kind)
            .ThenBy(snapshot => snapshot.Provider, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Profile, StringComparer.Ordinal)
            .ToArray();
    }

    private static DateTimeOffset? PreviousSuccess(TelemetrySnapshot snapshot, string provider) =>
        snapshot.Sources.FirstOrDefault(source => string.Equals(source.Provider, provider, StringComparison.OrdinalIgnoreCase))?.LastSuccessUtc;

    private static void ReplaceSource(IList<ProviderHealthSnapshot> sources, ProviderHealthSnapshot replacement)
    {
        var index = sources.ToList().FindIndex(source => string.Equals(source.Provider, replacement.Provider, StringComparison.OrdinalIgnoreCase));
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
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.00}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.0}M",
            >= 1_000 => $"{value / 1_000d:0.0}K",
            _ => value.ToString("N0")
        };
    }

    private static string FormatByteCount(long value)
    {
        var absolute = Math.Abs((double)value);
        return absolute switch
        {
            >= 1_073_741_824 => $"{value / 1_073_741_824d:0.00} GiB",
            >= 1_048_576 => $"{value / 1_048_576d:0.0} MiB",
            >= 1_024 => $"{value / 1_024d:0.0} KiB",
            _ => $"{value:N0} B"
        };
    }

    private static string SummarizeError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 320 ? message : message[..320] + "…";
    }
}
