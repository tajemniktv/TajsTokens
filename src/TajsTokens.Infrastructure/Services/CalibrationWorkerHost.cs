using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Infrastructure.Services;

public sealed record CalibrationWorkerRequest(string DatabasePath, string CachePath,
    QuotaLaneState[] Lanes, RolloutAccountAssociation[] Associations);

public static class CalibrationWorkerHost
{
    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        // Parent assigns the job before sending this request. No dataset is loaded before that.
        string? line = await input.ReadLineAsync();
        if (line is null || line.Length > 2 * 1024 * 1024) return 2;
        CalibrationWorkerRequest? request;
        try { request = JsonSerializer.Deserialize<CalibrationWorkerRequest>(line); }
        catch (JsonException) { return 2; }
        if (request is null || string.IsNullOrWhiteSpace(request.DatabasePath) || string.IsNullOrWhiteSpace(request.CachePath) ||
            !Path.IsPathFullyQualified(request.DatabasePath) ||
            !Path.IsPathFullyQualified(request.CachePath) || request.Lanes is null || request.Associations is null) return 2;
        try
        {
            using var cache = SqliteRidgeFitCache.ForCalibration(request.CachePath);
            using IDisposable scope = RidgeFitCacheScope.Use(cache);
            var repository = new SqliteTelemetryRepository(request.DatabasePath);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset from = request.Lanes.Where(x => x.IsFresh && x.Snapshot is not null)
                .Select(x => x.Snapshot!.CapturedAtUtc.AddDays(-120)).Append(now.AddDays(-30)).Min();
            if (from < now.AddDays(-121)) from = now.AddDays(-121);
            var datasetReader = new CalibrationDatasetReader(request.DatabasePath, request.Associations, from, now);
            var service = new SqliteIntelligenceService(request.DatabasePath, repository, () => request.Associations, datasetReader);
            IReadOnlyList<CurrentQuotaForecast> forecasts = await service.BuildAndPersistCurrentForecastsAsync(
                request.Lanes, now, CancellationToken.None);
            SqliteMemoryDiagnostics.Record(WorkPhase.QuotaCalibration);
            datasetReader.RetainOnlyNextRead();
            TokenWorkloadForecast workload = await service.ForecastTokenWorkloadAsync(now, CancellationToken.None);
            SqliteMemoryDiagnostics.Record(WorkPhase.WorkloadCalibration);
            await output.WriteLineAsync(JsonSerializer.Serialize(new CodexCalibrationResult(forecasts, workload, cache.Hits, cache.Misses)));
            await output.FlushAsync();
            return 0;
        }
        catch (Exception error) when (error is not StackOverflowException)
        {
            // Do not send source paths, account keys, raw observations, or exception payloads to logs.
            await output.WriteLineAsync("null");
            return 1;
        }
    }
}
