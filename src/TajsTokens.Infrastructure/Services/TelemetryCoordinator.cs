using System.Diagnostics;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Owns provider refresh serialization for the whole process. Dashboard, tray, alerts, and the
/// Phase 3 rollout observatory consume one refresh cadence rather than creating competing loops.
/// </summary>
public sealed class TelemetryCoordinator
{
    private readonly ITokscaleProvider _tokscaleProvider;
    private readonly ICodexQuotaProvider _quotaProvider;
    private readonly SqliteTelemetryRepository _repository;
    private readonly ICodexObservatoryService? _observatoryService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _activeRefreshSync = new();
    private CancellationTokenSource? _activeRefreshCancellation;
    private RefreshTrigger? _activeRefreshTrigger;
    private TelemetrySnapshot _latest = TelemetrySnapshot.Empty;

    public TelemetryCoordinator(
        ITokscaleProvider tokscaleProvider,
        ICodexQuotaProvider quotaProvider,
        SqliteTelemetryRepository repository,
        ICodexObservatoryService? observatoryService = null)
    {
        _tokscaleProvider = tokscaleProvider;
        _quotaProvider = quotaProvider;
        _repository = repository;
        _observatoryService = observatoryService;
    }

    public TelemetrySnapshot Latest => Volatile.Read(ref _latest);

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    public async Task<TelemetrySnapshot> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
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
            var sources = new List<ProviderHealthSnapshot>(4);
            var events = new List<TelemetryRefreshEvent>();

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

            // Rollout parsing can be the heavier first-run operation. Start it after the base schema is
            // ready, but let Tokscale/app-server I/O proceed concurrently so quota status is not delayed
            // by a historical local corpus scan any more than necessary.
            Task<CodexObservatoryRefreshResult>? observatoryTask = null;
            if (persistenceAvailable && _observatoryService is not null)
            {
                observatoryTask = _observatoryService.RefreshAsync(refreshToken);
            }

            var tokenUsages = previous.TokenUsages;
            var hourlyBuckets = previous.HourlyBuckets;
            var tokenFresh = false;
            try
            {
                var freshTokenUsages = await _tokscaleProvider.GetUsageObservationsAsync(refreshToken);
                var freshHourlyBuckets = await _tokscaleProvider.GetHourlyUsageAsync(refreshToken);
                tokenUsages = freshTokenUsages;
                hourlyBuckets = freshHourlyBuckets;
                tokenFresh = true;
                sources.Add(new ProviderHealthSnapshot(
                    "Tokscale",
                    TelemetryHealthState.Live,
                    $"{tokenUsages.Count} model row(s), {hourlyBuckets.Count} hourly bucket(s) from local Codex sessions.",
                    startedAt));
                events.Add(new TelemetryRefreshEvent(
                    startedAt,
                    "Token refresh",
                    $"Loaded {FormatTokenCount(tokenUsages.Sum(item => item.Breakdown.Total))} tokens from Tokscale."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var detail = SummarizeError(exception);
                var hasPrevious = previous.TokenUsages.Count > 0 || previous.HourlyBuckets.Count > 0;
                tokenUsages = previous.TokenUsages;
                hourlyBuckets = previous.HourlyBuckets;
                sources.Add(new ProviderHealthSnapshot(
                    "Tokscale",
                    hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                    hasPrevious ? $"Using last-known-good token data. {detail}" : detail,
                    PreviousSuccess(previous, "Tokscale")));
                events.Add(new TelemetryRefreshEvent(startedAt, "Tokscale unavailable", detail));
            }

            var quotaSnapshots = previous.QuotaSnapshots;
            IReadOnlyList<QuotaSnapshot> freshQuotaSnapshots = [];
            var quotaFresh = false;
            var quotaResponseHasSupportedWindow = false;
            try
            {
                freshQuotaSnapshots = await _quotaProvider.GetQuotaSnapshotsAsync(refreshToken);
                var supported = freshQuotaSnapshots
                    .Where(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly)
                    .ToArray();
                var hasFiveHour = supported.Any(snapshot => snapshot.Kind == QuotaWindowKind.FiveHour);
                var hasWeekly = supported.Any(snapshot => snapshot.Kind == QuotaWindowKind.Weekly);
                quotaResponseHasSupportedWindow = supported.Length > 0;
                quotaFresh = hasFiveHour && hasWeekly;

                if (quotaFresh)
                {
                    quotaSnapshots = freshQuotaSnapshots;
                }
                else if (quotaResponseHasSupportedWindow)
                {
                    quotaSnapshots = MergeQuotaSnapshots(previous.QuotaSnapshots, freshQuotaSnapshots);
                }

                var sourceState = quotaFresh
                    ? TelemetryHealthState.Live
                    : quotaResponseHasSupportedWindow
                        ? TelemetryHealthState.Stale
                        : TelemetryHealthState.Unavailable;
                var detail = quotaFresh
                    ? $"{supported.Length} supported provider-authoritative quota window(s). No model turn was created."
                    : quotaResponseHasSupportedWindow
                        ? "Codex returned only part of the supported quota set; observed lanes were merged with last-known-good data and the combined state is marked stale."
                        : "The app-server responded but did not expose a supported five-hour or weekly window.";

                sources.Add(new ProviderHealthSnapshot(
                    "Codex app-server",
                    sourceState,
                    detail,
                    quotaFresh ? startedAt : PreviousSuccess(previous, "Codex app-server")));
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
                var hasPrevious = previous.QuotaSnapshots.Count > 0;
                sources.Add(new ProviderHealthSnapshot(
                    "Codex app-server",
                    hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                    hasPrevious ? $"Using last-known-good quota data. {detail}" : detail,
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

            if (observatoryTask is not null)
            {
                try
                {
                    var observatory = await observatoryTask;
                    var state = observatory.Errors > 0 ? TelemetryHealthState.Stale : TelemetryHealthState.Live;
                    var detail = observatory.FilesDiscovered == 0
                        ? "No local Codex rollout JSONL sources were discovered."
                        : $"{observatory.FilesDiscovered} rollout file(s), {observatory.RecordsNormalized} new normalized record(s), {observatory.SessionsTouched} touched session(s), {FormatByteCount(observatory.BytesObserved)} observed on disk." +
                          (observatory.Errors > 0 ? $" {observatory.Errors} file(s) could not be refreshed and will retry." : string.Empty);
                    sources.Add(new ProviderHealthSnapshot("Codex rollouts", state, detail, startedAt));
                    events.Add(new TelemetryRefreshEvent(startedAt, "Codex observatory", detail));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
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
            stopwatch.Stop();
            events.Add(new TelemetryRefreshEvent(
                DateTimeOffset.UtcNow,
                "Refresh completed",
                $"{trigger.ToString().ToLowerInvariant()} refresh finished in {stopwatch.Elapsed.TotalSeconds:0.0}s."));

            var snapshotResult = new TelemetrySnapshot(
                DateTimeOffset.UtcNow,
                trigger,
                tokenUsages,
                hourlyBuckets,
                quotaSnapshots,
                tokenFresh,
                quotaFresh,
                persistenceAvailable,
                sources,
                events);

            Volatile.Write(ref _latest, snapshotResult);
            SnapshotUpdated?.Invoke(snapshotResult);
            return snapshotResult;
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
        catch
        {
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
                catch
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

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
