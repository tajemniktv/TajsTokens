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
                    CREATE TABLE codex_native_token_events (
                        source_event_id TEXT PRIMARY KEY, source_file TEXT, session_id TEXT,
                        observed_at_utc TEXT, uncached_input_tokens INTEGER, cache_read_tokens INTEGER,
                        cache_write_tokens INTEGER, non_reasoning_output_tokens INTEGER,
                        reasoning_output_tokens INTEGER, reported_total_tokens INTEGER);
                    INSERT INTO codex_native_token_events VALUES
                        ('a','private-command-text','private-session','2026-10-12T23:59:59.0000000+00:00',10,20,0,5,1,36),
                        ('b','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',3,4,1,2,1,11);
                    INSERT OR IGNORE INTO codex_native_token_events VALUES
                        ('b','private-command-text','private-session','2026-10-13T00:00:00.0000000+00:00',3,4,1,2,1,11);
                    """;
                await command.ExecuteNonQueryAsync();
            }
            byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var reader = new SqliteCodexDailyReportReader(path);
            CodexDailyTokenReport report = await reader.ReadAsync(
                DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
                DateTimeOffset.Parse("2026-10-14T00:00:00Z"), CancellationToken.None);
            Assert.Equal("tajstokens.daily-tokens.v1", report.Schema);
            Assert.Equal(2, report.Days.Count);
            Assert.Equal(new DateOnly(2026, 10, 12), report.Days[0].DayUtc);
            Assert.Equal(36, report.Days[0].ReportedTotal);
            Assert.Equal(new DateOnly(2026, 10, 13), report.Days[1].DayUtc);
            Assert.Equal(1, report.Days[1].Events);
            Assert.Equal(11, report.Days[1].ReportedTotal);
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
}
