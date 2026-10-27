using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Ingestion;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;

internal static class IngestionPerformanceCli
{
    // Synthetic, local-only fixtures. No native Codex paths, accounts or credentials are opened.
    public static async Task BenchmarkWritesAsync(string outputRoot, int count, bool reverse)
    {
        if (count is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(count));
        string fixture = CreateFixture(outputRoot);
        try
        {
            DateTimeOffset at = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
            CodexTokenCountObservation[] observations = Enumerable.Range(1, count).Select(i => new CodexTokenCountObservation(
                "e" + i, "rollout.jsonl", "session", null, at.AddSeconds(i), "model", "high",
                100L * i, 20L * i, 0, 10L * i, 2L * i, 110L * i) { CapturedAtUtc = at.AddDays(1) }).ToArray();
            foreach (bool batch in reverse ? new[] { true, false } : new[] { false, true })
            {
                string database = Path.Combine(fixture, batch ? "batch.db" : "single.db");
                await new SqliteTelemetryRepository(database).InitializeAsync(default);
                using var store = new SqliteCodexObservatoryStore(database);
                await store.InitializeAsync(default);
                using var process = Process.GetCurrentProcess();
                for (int replay = 0; replay < 2; replay++)
                {
                    process.Refresh(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    var watch = Stopwatch.StartNew();
                    if (batch)
                        foreach (var block in observations.Chunk(128)) await store.ApplyCumulativeTokenObservationsAsync(block, default);
                    else
                        foreach (var observation in observations) await store.ApplyCumulativeTokenObservationAsync(observation, default);
                    watch.Stop(); process.Refresh();
                    double cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
                    long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    var result = await new SqliteNativeCodexAccountingProvider(database).GetSnapshotAsync(default);
                    long total = result.Usage.Sum(item => item.Breakdown.Total);
                    if (total != 110L * count) throw new InvalidOperationException("Token conservation failed.");
                    Console.WriteLine(JsonSerializer.Serialize(new { count, batch, replay, elapsedMs = watch.Elapsed.TotalMilliseconds,
                        cpuMs, allocatedBytes, total, transactionBatches = batch ? (count + 127) / 128 : count,
                        qualification = "Synthetic fixture; same current reducer, triggers and WAL. Not a released-binary or live-profile timing." }));
                }
            }
        }
        finally { RemoveFixture(outputRoot, fixture); }
    }

    public static async Task BenchmarkPrefixAsync(string outputRoot)
    {
        string fixture = CreateFixture(outputRoot);
        try
        {
            string database = Path.Combine(fixture, "data.db"), file = Path.Combine(fixture, "rollout.jsonl");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            string line = JsonSerializer.Serialize(new { type = "noop", padding = new string('x', 1024) }) + "\n";
            await File.WriteAllTextAsync(file, string.Concat(Enumerable.Repeat(line, 1024)));
            var ingestion = new CodexSessionIngestionService(new FileSystemCodexSessionEventProvider(), repository);
            await Measure("cold");
            await Measure("unchanged");
            await File.AppendAllTextAsync(file, line);
            await Measure("append");
            // Rewrite a consumed byte without changing length; make the metadata change explicit.
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
            { stream.Position = line.IndexOf(new string('x', 16), StringComparison.Ordinal); stream.WriteByte((byte)'y'); }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
            await Measure("rewritten-prefix");

            async Task Measure(string phase)
            {
                var watch = Stopwatch.StartNew();
                var result = await ingestion.IngestAsync(file, default);
                watch.Stop();
                Console.WriteLine(JsonSerializer.Serialize(new { phase, elapsedMs = watch.Elapsed.TotalMilliseconds,
                    result.SourceLength, result.RecordsScanned, result.PrefixBytesHashed,
                    result.CompleteRecordBytesDecoded, result.CheckpointWrites,
                    qualification = "Synthetic boundary-ingestion fixture. Logical hash/complete-record bytes, not physical I/O." }));
            }
        }
        finally { RemoveFixture(outputRoot, fixture); }
    }

    private static string CreateFixture(string outputRoot)
    {
        string path = Path.Combine(Path.GetFullPath(outputRoot), "ingestion-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void RemoveFixture(string outputRoot, string fixture)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot)) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(fixture);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped output root.");
        SqliteConnection.ClearAllPools();
        Directory.Delete(target, true);
    }
}
