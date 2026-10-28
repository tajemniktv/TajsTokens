using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

public sealed class CalibrationProcessService : ICodexCalibrationService, IDisposable
{
    private readonly string _executable;
    private readonly string _databasePath;
    private readonly Func<RolloutAccountAssociation[]> _associations;
    private readonly Func<bool> _isBackground;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CalibrationWorkerJob? _job;
    private DateTimeOffset _nextAttempt;
    private bool _lastBackground;
    private bool _disposed;
    private readonly SqliteCalibrationEvidence _evidence;
    private readonly Func<CalibrationWorkerRequest, CancellationToken, Task<CodexCalibrationResult?>> _runWorker;
    private CompletedCalibration? _completed;
    private DateTimeOffset _lastObservedAt;

    public CalibrationProcessService(string executable, string databasePath,
        Func<RolloutAccountAssociation[]> associations, Func<bool> isBackground, TimeProvider? clock = null)
    {
        _executable = executable;
        _databasePath = databasePath;
        _associations = associations;
        _isBackground = isBackground;
        _clock = clock ?? TimeProvider.System;
        _lastBackground = isBackground();
        _evidence = new(databasePath);
        _runWorker = RunWorkerAsync;
    }

    internal CalibrationProcessService(string executable, string databasePath,
        Func<RolloutAccountAssociation[]> associations, Func<bool> isBackground, TimeProvider clock,
        Func<CalibrationWorkerRequest, CancellationToken, Task<CodexCalibrationResult?>> runWorker)
        : this(executable, databasePath, associations, isBackground, clock) => _runWorker = runWorker;

    public void RefreshBudget()
    {
        lock (_sync)
        {
            if (_disposed) return;
            bool background = _isBackground();
            if (_lastBackground && !background) _nextAttempt = default;
            _lastBackground = background;
            try { _job?.SetBackground(background); }
            catch (System.ComponentModel.Win32Exception) { _job?.Dispose(); _job = null; }
        }
    }

    public async Task<CodexCalibrationResult?> TryCalibrateAsync(IReadOnlyList<QuotaLaneState> lanes, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        bool attempted = false;
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            lock (_sync)
            {
                if (now < _lastObservedAt) { _completed = null; _nextAttempt = default; }
                _lastObservedAt = now;
                if (_disposed || now < _nextAttempt || !lanes.Any(x => x.IsFresh && x.Snapshot is not null)) return null;
            }
            using var work = WorkDiagnostics.Measure(WorkPhase.CalibrationSlice);
            CalibrationEvidenceStamp? before = await _evidence.ReadAsync(now, cancellationToken).ConfigureAwait(false);
            RolloutAccountAssociation[] associations = _associations().ToArray();
            string? key = Key(before, lanes, associations);
            if (key is not null && _completed is { } completed && completed.Key == key &&
                now >= completed.AtUtc && now < completed.ValidUntilUtc)
            {
                work?.Complete(cacheHits: 1);
                // Preserve the actual forecast/anchor times. Reuse never relabels an old
                // forecast as a newly generated one; cheap current pace runs independently.
                return completed.Result;
            }
            attempted = true;
            var request = new CalibrationWorkerRequest(_databasePath,
                Path.Combine(Path.GetDirectoryName(_databasePath)!, "calibration-cache.db"), lanes.ToArray(), associations);
            CodexCalibrationResult? result = await _runWorker(request, cancellationToken).ConfigureAwait(false);
            if (result is null) return null;
            DateTimeOffset finished = _clock.GetUtcNow();
            CalibrationEvidenceStamp? after = await _evidence.ReadAsync(finished, cancellationToken).ConfigureAwait(false);
            string? afterKey = Key(after, lanes, _associations());
            if (key is not null && afterKey is not null && key != afterKey)
            {
                _completed = null;
                // Fits completed inside the slice survive. Its result cannot claim that
                // evidence changed during the worker's reads belongs to this generation.
                // Keep the actual older result available as stale rather than erasing it.
                result = result with
                {
                    Forecasts = result.Forecasts.Select(x => x with { State = TelemetryHealthState.Stale }).ToArray(),
                    Workload = result.Workload is { } old ? old with { IsStale = true } : null,
                };
                work?.Complete(cacheHits: result.CacheHits);
                return result;
            }
            if (result.Workload is { } workload && (finished < workload.GeneratedAtUtc ||
                workload.Predictions.Any(x => workload.GeneratedAtUtc.AddHours(x.HorizonHours) <= finished)))
                result = result with { Workload = workload with { IsStale = true } };
            if (key is not null && key == afterKey && finished >= now)
                _completed = new(key, now, ValidUntil(result, before!), result);
            else _completed = null;
            work?.Complete(cacheHits: result.CacheHits);
            return result;
        }
        finally
        {
            lock (_sync)
                if (attempted) _nextAttempt = _clock.GetUtcNow().AddMinutes(_isBackground() ? 5 : 1);
            _gate.Release();
        }
    }

    private static string? Key(CalibrationEvidenceStamp? stamp, IReadOnlyList<QuotaLaneState> lanes,
        RolloutAccountAssociation[] associations) => stamp is null ? null : Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                stamp.StorageIdentity, stamp.Revision,
                Lanes = lanes.Select(x => new { x.Provider, x.Profile, x.Kind, x.IsFresh, x.Snapshot }),
                Associations = associations,
            })));

    private static DateTimeOffset ValidUntil(CodexCalibrationResult result, CalibrationEvidenceStamp stamp)
    {
        var boundaries = new List<DateTimeOffset>();
        if (stamp.NextTimeBoundaryUtc is { } next) boundaries.Add(next);
        if (result.Workload is { } workload)
        {
            boundaries.AddRange(workload.Predictions.Select(x => workload.GeneratedAtUtc.AddHours(x.HorizonHours)));
            if (workload.Activity is { SupportsNowcast: true, LastActivityAtUtc: { } activity })
                boundaries.Add(activity + CodexNowcastActivity.IdleAfter);
            else if (workload.Activity is null && workload.Predictions.Count > 0 && workload.LatestTokenAtUtc is { } last)
                boundaries.Add(last + CodexNowcastActivity.IdleAfter);
        }
        foreach (CurrentQuotaForecast forecast in result.Forecasts.Where(x => x.IsFresh))
        {
            if (forecast.Current.ResetsAtUtc is { } reset) boundaries.Add(reset);
            if (forecast.Forecast?.Evidence?.HorizonPredictions is { } horizons)
                boundaries.AddRange(horizons.Select(x => forecast.Current.CapturedAtUtc.AddHours(x.HorizonHours)));
        }
        // An already expired result cannot suppress another attempt after cooldown.
        return boundaries.Count == 0 ? DateTimeOffset.MaxValue : boundaries.Min();
    }

    private sealed record CompletedCalibration(string Key, DateTimeOffset AtUtc, DateTimeOffset ValidUntilUtc,
        CodexCalibrationResult Result);

    private async Task<CodexCalibrationResult?> RunWorkerAsync(CalibrationWorkerRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // Completed fits are committed separately, so later slices can reuse partial progress.
        // At the 0.5% cap a useful historical pass takes minutes. A tiny wall timeout would
        // repeatedly discard dataset preparation before reaching any reusable fit.
        timeout.CancelAfter(_isBackground() ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3));
        CancellationToken token = timeout.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_executable, "--calibration-worker")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_executable)!,
            },
        };
        process.StartInfo.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // 256 MiB managed heap
        process.StartInfo.Environment["DOTNET_GCConserveMemory"] = "5";
        if (!process.Start()) throw new IOException("Calibration worker could not start.");
        Task<string>? output = null;
        Task<string>? errors = null;
        try
        {
            lock (_sync)
            {
                if (_disposed) throw new OperationCanceledException(_lifetime.Token);
                _job = new CalibrationWorkerJob(process.Handle, _isBackground());
            }
            output = ReadBoundedAsync(process.StandardOutput, 4 * 1024 * 1024, token);
            errors = ReadBoundedAsync(process.StandardError, 8192, token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            string json = await output;
            await errors; // Observed and discarded: never log content-bearing errors.
            if (process.ExitCode != 0) throw new IOException("Calibration worker stopped without a complete result; quota-pace outlook retained.");
            CodexCalibrationResult? result = JsonSerializer.Deserialize<CodexCalibrationResult>(json);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Calibration slice reached its time budget; completed numeric fits are cached for the next slice.");
        }
        finally
        {
            lock (_sync) { _job?.Dispose(); _job = null; }
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            timeout.Cancel();
            // Observe both pipe readers on every exit path, including a killed/timed-out slice.
            foreach (Task<string>? reader in new[] { output, errors })
            {
                if (reader is null) continue;
                try { await reader; }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        bool overflow = false;
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + read > limit) overflow = true;
            else if (!overflow) text.Append(buffer, 0, read);
        }
        if (overflow) throw new InvalidDataException("Calibration worker output exceeded its bound.");
        return text.ToString();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _completed = null;
            _lifetime.Cancel();
            _job?.Dispose();
            _job = null;
        }
    }
}
