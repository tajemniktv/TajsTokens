using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;

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

    public CalibrationProcessService(string executable, string databasePath,
        Func<RolloutAccountAssociation[]> associations, Func<bool> isBackground, TimeProvider? clock = null)
    {
        _executable = executable;
        _databasePath = databasePath;
        _associations = associations;
        _isBackground = isBackground;
        _clock = clock ?? TimeProvider.System;
        _lastBackground = isBackground();
    }

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
            lock (_sync)
            {
                if (_disposed || _clock.GetUtcNow() < _nextAttempt || !lanes.Any(x => x.IsFresh && x.Snapshot is not null)) return null;
                attempted = true;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            using var work = WorkDiagnostics.Measure(WorkPhase.CalibrationSlice);
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
                var request = new CalibrationWorkerRequest(_databasePath,
                    Path.Combine(Path.GetDirectoryName(_databasePath)!, "calibration-cache.db"), lanes.ToArray(), _associations());
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token);
                string json = await output;
                await errors; // Observed and discarded: never log content-bearing errors.
                if (process.ExitCode != 0) throw new IOException("Calibration worker stopped without a complete result; quota-pace outlook retained.");
                CodexCalibrationResult? result = JsonSerializer.Deserialize<CodexCalibrationResult>(json);
                work?.Complete(cacheHits: result?.CacheHits ?? -1);
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
        finally
        {
            lock (_sync)
                if (attempted) _nextAttempt = _clock.GetUtcNow().AddMinutes(_isBackground() ? 5 : 1);
            _gate.Release();
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
            _lifetime.Cancel();
            _job?.Dispose();
            _job = null;
        }
    }
}
