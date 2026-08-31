using System.Diagnostics;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

/// <summary>
/// Owns provider refresh serialization for the whole process. Dashboard, tray and alerts consume the
/// same normalized snapshot so opening another surface cannot accidentally create a second polling
/// loop or erase last-known-good data after a transient provider failure.
/// </summary>
public sealed class TelemetryCoordinator
{
    private readonly ITokscaleProvider _tokscaleProvider;
    private readonly ICodexQuotaProvider _quotaProvider;
    private readonly SqliteTelemetryRepository _repository;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TelemetrySnapshot _latest = TelemetrySnapshot.Empty;

    public TelemetryCoordinator(
        ITokscaleProvider tokscaleProvider,
        ICodexQuotaProvider quotaProvider,
        SqliteTelemetryRepository repository)
    {
        _tokscaleProvider = tokscaleProvider;
        _quotaProvider = quotaProvider;
        _repository = repository;
    }

    public TelemetrySnapshot Latest => Volatile.Read(ref _latest);

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    public async Task<TelemetrySnapshot> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var previous = Latest;
            var sources = new List<ProviderHealthSnapshot>(3);
            var events = new List<TelemetryRefreshEvent>();

            var persistenceAvailable = false;
            try
            {
                await _repository.InitializeAsync(cancellationToken);
                persistenceAvailable = true;
                sources.Add(new ProviderHealthSnapshot(
                    "SQLite",
                    TelemetryHealthState.Live,
                    "Local quota history is available.",
                    startedAt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var detail = SummarizeError(exception);
                sources.Add(new ProviderHealthSnapshot("SQLite", TelemetryHealthState.Error, detail));
                events.Add(new TelemetryRefreshEvent(startedAt, "Persistence unavailable", detail));
            }

            var tokenUsages = previous.TokenUsages;
            var hourlyBuckets = previous.HourlyBuckets;
            var tokenFresh = false;
            try
            {
                tokenUsages = await _tokscaleProvider.GetUsageObservationsAsync(cancellationToken);
                hourlyBuckets = await _tokscaleProvider.GetHourlyUsageAsync(cancellationToken);
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
                sources.Add(new ProviderHealthSnapshot(
                    "Tokscale",
                    hasPrevious ? TelemetryHealthState.Stale : TelemetryHealthState.Unavailable,
                    hasPrevious ? $"Using last-known-good token data. {detail}" : detail,
                    PreviousSuccess(previous, "Tokscale")));
                events.Add(new TelemetryRefreshEvent(startedAt, "Tokscale unavailable", detail));
            }

            var quotaSnapshots = previous.QuotaSnapshots;
            var quotaFresh = false;
            try
            {
                var fresh = await _quotaProvider.GetQuotaSnapshotsAsync(cancellationToken);
                quotaFresh = fresh.Any(snapshot => snapshot.Kind is QuotaWindowKind.FiveHour or QuotaWindowKind.Weekly);
                if (quotaFresh)
                {
                    quotaSnapshots = fresh;
                }

                sources.Add(new ProviderHealthSnapshot(
                    "Codex app-server",
                    quotaFresh ? TelemetryHealthState.Live : TelemetryHealthState.Unavailable,
                    quotaFresh
                        ? $"{fresh.Count} provider-authoritative quota window(s). No model turn was created."
                        : "The app-server responded but did not expose a supported five-hour or weekly window.",
                    quotaFresh ? startedAt : PreviousSuccess(previous, "Codex app-server")));
                events.Add(new TelemetryRefreshEvent(
                    startedAt,
                    "Quota refresh",
                    quotaFresh
                        ? "Captured provider-authoritative Codex quota and reset timestamps."
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

            if (persistenceAvailable && quotaFresh)
            {
                try
                {
                    foreach (var snapshot in quotaSnapshots)
                    {
                        await _repository.UpsertQuotaSnapshotAsync(snapshot, cancellationToken);
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
                        $"Live quota is still available, but this snapshot was not stored: {detail}"));
                }
            }

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
            // Individual provider failures are normalized into snapshots. This outer guard is only
            // for an unexpected coordinator failure; the periodic chain must still survive it.
        }

        using var timer = new PeriodicTimer(interval);
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
                // Keep the next interval alive. The dashboard/tray retain the previous snapshot.
            }
        }
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

    private static string SummarizeError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ").Trim();
        return message.Length <= 320 ? message : message[..320] + "…";
    }
}
