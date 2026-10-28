// Taj's Tokens | SqliteCodexDailyReportReaderTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class SqliteCodexDailyReportReaderTests
{
    [Fact]
    public async Task GroupsCanonicalEventsByUtcDayWithoutContentOrAccountClaims()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        string tempRoot = Path.Combine(root.FullName, ".codex", "temp");
        Directory.CreateDirectory(tempRoot);
        string directory = Path.Combine(tempRoot, "daily-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "telemetry.db");
        try
        {
            await using (var connection = new SqliteConnection(
                             new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE observatory_schema(component TEXT PRIMARY KEY, version INTEGER NOT NULL);
                    INSERT INTO observatory_schema VALUES('codex-observatory',11);
                    CREATE TABLE codex_native_token_events (
                        source_event_id TEXT PRIMARY KEY, source_file TEXT, session_id TEXT,
                        observed_at_utc TEXT, uncached_input_tokens INTEGER, cache_read_tokens INTEGER,
                        cache_write_tokens INTEGER, non_reasoning_output_tokens INTEGER,
                        reasoning_output_tokens INTEGER, reported_total_tokens INTEGER,
                        source_event_at_utc TEXT, source_timestamp_quality TEXT, model TEXT);
                    INSERT INTO codex_native_token_events VALUES
                        ('a','private-command-text','private-session','2026-10-14T00:00:00.0000000+00:00',10,20,0,5,1,36,'2026-10-12T23:59:59.0000000+00:00','Valid',NULL),
                        ('b','private-command-text','private-session','2026-10-12T23:00:00.0000000+00:00',3,4,1,2,1,11,'2026-10-13T00:00:00.0000000+00:00','Valid',NULL),
                        ('legacy','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',111,0,0,0,0,111,'2026-10-13T00:00:00.0000000+00:00','LegacyUnverified',NULL),
                        ('missing','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',222,0,0,0,0,222,NULL,'Missing',NULL),
                        ('malformed','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',333,0,0,0,0,333,'bad-date','Malformed',NULL),
                        ('zero','private-command-text','private-session','2026-10-13T01:00:00.0000000+00:00',0,0,0,0,0,0,'2026-10-13T01:00:00.0000000+00:00','Valid',NULL);
                    INSERT OR IGNORE INTO codex_native_token_events VALUES
                        ('b','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',3,4,1,2,1,11,'2026-10-13T00:00:00.0000000+00:00','Valid',NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var reader = new SqliteCodexDailyReportReader(path);
            CodexDailyTokenReport report = await reader.ReadAsync(
                DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
                DateTimeOffset.Parse("2026-10-14T00:00:00Z"), CancellationToken.None);
            Assert.Equal("tajstokens.daily-tokens.v2", report.Schema);
            Assert.Equal(2, report.Days.Count);
            Assert.Equal(new DateOnly(2026, 10, 12), report.Days[0].DayUtc);
            Assert.Equal(36, report.Days[0].ReportedTotal);
            Assert.Equal(new DateOnly(2026, 10, 13), report.Days[1].DayUtc);
            Assert.Equal(2, report.Days[1].Events); // An observed zero is retained; undated/legacy amounts are not dated zeros.
            Assert.Equal(11, report.Days[1].ReportedTotal);
            Assert.Equal(new CodexNativeTokenCoverage(6, 3, 47, 3, 666, 6, 713), report.AllTimeCoverage);
            string json = JsonSerializer.Serialize(report);
            Assert.DoesNotContain("private-command-text", json);
            Assert.DoesNotContain("private-session", json);
            Assert.Contains("unknown", report.AccountAttribution);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    public async Task RefusesUnsupportedChronologySchemaWithoutChangingTheDatabase(int version)
    {
        using var f = new CalibrationEvidenceTests.Fixture();
        await f.InitializeAsync();
        await f.SqlAsync($"UPDATE observatory_schema SET version={version} WHERE component='codex-observatory';");
        SqliteConnection.ClearAllPools();
        byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(f.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteCodexDailyReportReader(f.Path)
            .ReadAsync(f.Now.AddDays(-1), f.Now, default));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(f.Path)));
    }
}
