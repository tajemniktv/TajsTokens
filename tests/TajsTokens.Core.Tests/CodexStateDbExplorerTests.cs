// Taj's Tokens | CodexStateDbExplorerTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using Microsoft.Data.Sqlite;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexStateDbExplorerTests
{
    [Fact]
    public async Task DiscoverAndInspect_ReportsRawSchemaWithoutUsingProductModels()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-explorer-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);

            IReadOnlyList<CodexStateDatabaseCandidate> candidates = explorer.DiscoverCandidates();
            CodexStateDatabaseCandidate candidate = Assert.Single(candidates);
            Assert.Equal(path, candidate.Path);
            Assert.Equal(5, candidate.Generation);

            CodexStateInspectionResult result = await explorer.InspectAsync(path);
            Assert.Equal(path, result.Database.Path);
            CodexStateTableInfo threads = Assert.Single(result.Tables, table => table.Name == "threads");
            Assert.Equal("table", threads.ObjectType);
            Assert.Equal(2, threads.RowCount);
            Assert.Contains(threads.Columns, column => column.Name == "prompt");
            Assert.Contains(threads.Indexes, index => index.Name == "idx_threads_model");
            Assert.Contains(result.FocusedTables, table => table.Name == "threads");
            Assert.Matches("^[0-9A-F]{64}$", result.Snapshot.SchemaFingerprint);
            Assert.True(CodexStateDbExplorerService.IsInterestingTable("logs"));
            Assert.True(CodexStateDbExplorerService.IsInterestingTable("thread_turn_summaries"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DiscoverCandidates_IncludesSnapshotFilesAndCodexSqliteFolderDbs()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-discovery-");
        try
        {
            DirectoryInfo codexHome = Directory.CreateDirectory(Path.Combine(directory.FullName, ".codex"));
            DirectoryInfo sqliteDirectory = Directory.CreateDirectory(Path.Combine(codexHome.FullName, "sqlite"));
            DirectoryInfo snapshots = Directory.CreateDirectory(Path.Combine(directory.FullName, "private"));
            foreach (string path in new[]
                     {
                         Path.Combine(codexHome.FullName, "state_5.sqlite"),
                         Path.Combine(sqliteDirectory.FullName, "codex-dev.db"),
                         Path.Combine(sqliteDirectory.FullName, "codex-thread-summaries-dev.db"),
                         Path.Combine(snapshots.FullName, "state_5_snapshot.sqlite"),
                         Path.Combine(snapshots.FullName, "logs_2_snapshot.sqlite"),
                     })
            {
                await File.WriteAllBytesAsync(path, []);
            }

            var explorer = new CodexStateDbExplorerService(codexHome.FullName, snapshots.FullName);
            IReadOnlyList<CodexStateDatabaseCandidate> candidates = explorer.DiscoverCandidates();

            Assert.Equal(5, candidates.Count);
            Assert.Contains(
                candidates,
                candidate => candidate.FileName == "codex-dev.db" && candidate.SourceDescription == "Codex sqlite folder");
            Assert.Contains(
                candidates,
                candidate => candidate.FileName == "codex-thread-summaries-dev.db" && candidate.SourceDescription == "Codex sqlite folder");
            Assert.Contains(
                candidates,
                candidate => candidate.FileName == "logs_2_snapshot.sqlite" && candidate.SourceDescription == "Snapshot folder");
            Assert.Equal(5, Assert.Single(candidates, candidate => candidate.FileName == "state_5_snapshot.sqlite").Generation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ReadPage_UsesReadOnlyQueryOnlyAndReturnsRawValues()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-page-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);

            CodexStateRawPage page = await explorer.ReadPageAsync(path, "threads", 0, 1);
            Assert.Equal(2, page.TotalRows);
            Assert.Single(page.Rows);
            Assert.Equal(["id", "model", "prompt", "rollout_path", "payload"], page.Columns);
            Assert.Contains("thread-1", page.Rows[0].DisplayText);
            Assert.Contains("prompt one", page.Rows[0].DisplayText);

            await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await using var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await connection.OpenAsync();
                SqliteCommand command = connection.CreateCommand();
                command.CommandText = "PRAGMA query_only = ON; INSERT INTO threads(id) VALUES('blocked');";
                await command.ExecuteNonQueryAsync();
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Compare_DetectsChangedRowsWithAnUnchangedRowCount()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-diff-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateInspectionSnapshot baseline = (await explorer.InspectAsync(path)).Snapshot;

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE threads SET model = 'gpt-new' WHERE id = 'thread-2';";
                await update.ExecuteNonQueryAsync();
            }

            CodexStateInspectionSnapshot current = (await explorer.InspectAsync(path)).Snapshot;
            CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(baseline, current);
            CodexStateTableDiff changed = Assert.Single(diff.Tables, table => table.Name == "threads");
            Assert.True(changed.RowsChanged);
            Assert.False(changed.SchemaChanged);
            Assert.False(diff.SchemaChanged);
            Assert.Equal(2, changed.PreviousRowCount);
            Assert.Equal(2, changed.CurrentRowCount);
            Assert.Contains(
                changed.RowChanges,
                row => row.ChangeKind == "changed" && row.RowIdentity.Contains("thread-2", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Compare_ReportsAddedRemovedAndChangedRowCandidatesWithSourceProvenance()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-row-diff-");
        try
        {
            string baselinePath = Path.Combine(directory.FullName, "state_5.sqlite");
            string currentPath = Path.Combine(directory.FullName, "state_6.sqlite");
            await CreateDatabaseAsync(baselinePath);
            File.Copy(baselinePath, currentPath);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = currentPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand update = connection.CreateCommand();
                update.CommandText =
                    "UPDATE threads SET model = 'gpt-new' WHERE id = 'thread-2'; DELETE FROM threads WHERE id = 'thread-1'; INSERT INTO threads(id, model) VALUES ('thread-3', 'gpt-c');";
                await update.ExecuteNonQueryAsync();
            }

            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateInspectionSnapshot baseline = (await explorer.InspectAsync(baselinePath)).Snapshot;
            CodexStateInspectionSnapshot current = (await explorer.InspectAsync(currentPath)).Snapshot;
            CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(baseline, current);
            CodexStateTableDiff table = Assert.Single(diff.Tables, candidate => candidate.Name == "threads");

            Assert.True(table.RowComparisonComplete);
            Assert.False(table.RowComparisonBounded);
            Assert.Equal(3, table.RowChanges.Count);
            Assert.Contains(
                table.RowChanges,
                row => row.ChangeKind == "removed" && row.RowIdentity.Contains("thread-1", StringComparison.Ordinal));
            Assert.Contains(
                table.RowChanges,
                row => row.ChangeKind == "changed" && row.RowIdentity.Contains("thread-2", StringComparison.Ordinal));
            Assert.Contains(
                table.RowChanges,
                row => row.ChangeKind == "added" && row.RowIdentity.Contains("thread-3", StringComparison.Ordinal));
            Assert.All(
                table.RowChanges,
                row =>
                {
                    Assert.Equal(Path.GetFullPath(baselinePath), row.BaselineDatabasePath);
                    Assert.Equal(Path.GetFullPath(currentPath), row.CurrentDatabasePath);
                    Assert.Equal("primary-key", row.IdentityKind);
                });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Compare_UnchangedSmallSourceHasCompleteRowEvidenceAndNoDiffs()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-row-unchanged-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(path);
            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateInspectionSnapshot first = (await explorer.InspectAsync(path)).Snapshot;
            CodexStateInspectionSnapshot second = (await explorer.InspectAsync(path)).Snapshot;
            CodexStateTableSnapshot threads = Assert.Single(first.Tables, table => table.Name == "threads");

            Assert.True(threads.RowComparisonComplete);
            Assert.Equal(2, threads.RowObservationCount);
            CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(first, second);
            Assert.False(diff.HasChanges);
            Assert.False(diff.HasIncompleteComparisons);
            Assert.Empty(diff.Tables);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public void Compare_ReportsCountOnlyCoverageWhenRowsWereNotCaptured()
    {
        var before = new CodexStateInspectionSnapshot(
            "before.sqlite",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            [new CodexStateTableSnapshot("threads", "table", 10, "schema", "rows")])
        {
            SourceDescription = "baseline",
            RowObservations = new Dictionary<string, IReadOnlyList<CodexStateRowObservation>>(StringComparer.OrdinalIgnoreCase),
        };
        var after = new CodexStateInspectionSnapshot(
            "after.sqlite",
            DateTimeOffset.UtcNow,
            [
                new CodexStateTableSnapshot("threads", "table", 10, "schema", "rows")
                {
                    RowComparisonBounded = true, RowComparisonNote = "count-only: database exceeds 64 MiB",
                },
            ])
        {
            SourceDescription = "current",
            RowObservations = new Dictionary<string, IReadOnlyList<CodexStateRowObservation>>(StringComparer.OrdinalIgnoreCase),
        };

        CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(before, after);
        CodexStateTableDiff table = Assert.Single(diff.Tables);
        Assert.Equal("incomplete", table.ChangeKind);
        Assert.False(table.RowsChanged);
        Assert.True(diff.HasChanges);
        Assert.True(diff.HasIncompleteComparisons);
        Assert.Contains("count-only", table.EvidenceSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compare_DoesNotTreatDifferentFingerprintModesAsAContentChange()
    {
        var before = new CodexStateInspectionSnapshot(
            "before.sqlite",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            [
                new CodexStateTableSnapshot("threads", "table", 2, "schema", "full-fingerprint")
                {
                    RowComparisonComplete = true, RowFingerprintMode = "full", RowObservationCount = 2,
                },
            ]);
        var after = new CodexStateInspectionSnapshot(
            "after.sqlite",
            DateTimeOffset.UtcNow,
            [
                new CodexStateTableSnapshot("threads", "table", 2, "schema", "count-fingerprint")
                {
                    RowComparisonBounded = true,
                    RowFingerprintMode = "count-only",
                    RowComparisonNote = "count-only inspection requested",
                },
            ]);

        CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(before, after);
        CodexStateTableDiff table = Assert.Single(diff.Tables);
        Assert.Equal("incomplete", table.ChangeKind);
        Assert.False(table.RowsChanged);
        Assert.False(table.HasRowLevelChanges);
        Assert.Contains("fingerprint modes differ", table.EvidenceSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_LargeTableUsesBoundedCountOnlyEvidence()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-large-table-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    "CREATE TABLE large_rows(id INTEGER PRIMARY KEY, value TEXT); WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 100001) INSERT INTO large_rows(id, value) SELECT value, 'filler' FROM numbers;";
                await command.ExecuteNonQueryAsync();
            }

            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateInspectionSnapshot snapshot = (await explorer.InspectAsync(path)).Snapshot;
            CodexStateTableSnapshot table = Assert.Single(snapshot.Tables);

            Assert.Equal(100001, table.RowCount);
            Assert.False(table.RowComparisonComplete);
            Assert.True(table.RowComparisonBounded);
            Assert.Empty(snapshot.RowObservations["large_rows"]);
            Assert.Contains("count-only", table.RowComparisonNote, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SanitizedExport_RedactsContentAndReducesPathsAndBlobs()
    {
        var page = new CodexStateRawPage(
            "C:\\Users\\tester\\.codex\\state_5.sqlite",
            "threads",
            0,
            50,
            1,
            ["id", "prompt", "rollout_path", "blob_value"],
            [new CodexStateRawRow(["id-1", "private prompt", "C:\\Users\\tester\\rollout.jsonl", new byte[] { 1, 2 }], "")]);

        string export = CodexStateDbExplorerService.BuildSanitizedExport(page);
        Assert.Contains("id-1", export);
        Assert.Contains("[redacted]", export);
        Assert.Contains("<path:rollout.jsonl>", export);
        Assert.Contains("<blob 2 bytes>", export);
        Assert.DoesNotContain("private prompt", export);
    }

    [Fact]
    public async Task CombinedSchemaExport_LabelsEachDatabaseWithoutExportingRows()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-schema-");
        try
        {
            string firstPath = Path.Combine(directory.FullName, "state_5_snapshot.sqlite");
            string secondPath = Path.Combine(directory.FullName, "codex-dev.db");
            await CreateDatabaseAsync(firstPath);
            File.Copy(firstPath, secondPath);

            var explorer = new CodexStateDbExplorerService(directory.FullName, directory.FullName);
            CodexStateInspectionResult first = await explorer.InspectAsync(firstPath, false);
            CodexStateInspectionResult second = await explorer.InspectAsync(secondPath, false);
            string export = CodexStateDbExplorerService.BuildCombinedSchemaExport([first, second]);

            Assert.Contains($"-- SOURCE: {Path.GetFullPath(firstPath)}", export);
            Assert.Contains($"-- SOURCE: {Path.GetFullPath(secondPath)}", export);
            Assert.Contains($"-- SCHEMA FINGERPRINT: {first.Snapshot.SchemaFingerprint}", export);
            Assert.Contains("CREATE TABLE threads", export);
            Assert.Contains("-- COLUMN 0: id TEXT", export);
            Assert.DoesNotContain("prompt one", export);
            Assert.DoesNotContain("thread-1", export);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Compare_ReportsSchemaFingerprintAndCrossSourceChanges()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-schema-diff-");
        try
        {
            string firstPath = Path.Combine(directory.FullName, "state_5.sqlite");
            string secondPath = Path.Combine(directory.FullName, "state_6.sqlite");
            await CreateDatabaseAsync(firstPath);
            File.Copy(firstPath, secondPath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = secondPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE threads ADD COLUMN source_marker TEXT;";
                await alter.ExecuteNonQueryAsync();
            }

            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateInspectionResult first = await explorer.InspectAsync(firstPath, false);
            CodexStateInspectionResult second = await explorer.InspectAsync(secondPath, false);
            CodexStateInspectionDiff diff = CodexStateDbExplorerService.Compare(first.Snapshot, second.Snapshot);

            Assert.NotEqual(first.Snapshot.SchemaFingerprint, second.Snapshot.SchemaFingerprint);
            Assert.True(diff.SchemaChanged);
            Assert.Equal(firstPath, diff.BaselineDatabasePath);
            Assert.Equal(secondPath, diff.CurrentDatabasePath);
            Assert.Contains(
                diff.Tables,
                table => table.Name == "threads" && table.SchemaChanged && table.AddedColumns.Contains("source_marker"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task TraceKey_SearchesExactSourceColumnAcrossDatabasesWithoutJoining()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-trace-");
        try
        {
            string firstPath = Path.Combine(directory.FullName, "state_5.sqlite");
            string secondPath = Path.Combine(directory.FullName, "queue_1_snapshot.sqlite");
            await CreateDatabaseAsync(firstPath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = secondPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand schema = connection.CreateCommand();
                schema.CommandText =
                    "CREATE TABLE queue_items(thread_id TEXT, state TEXT); INSERT INTO queue_items VALUES ('thread-2', 'queued'), ('other', 'ignored');";
                await schema.ExecuteNonQueryAsync();
            }

            var explorer = new CodexStateDbExplorerService(directory.FullName);
            CodexStateKeyTraceResult trace = await explorer.TraceKeyAsync([firstPath, secondPath], "thread_id", "thread-2");

            CodexStateKeyTraceMatch match = Assert.Single(trace.Matches);
            Assert.Equal(Path.GetFullPath(secondPath), match.DatabasePath);
            Assert.Equal("queue_items", match.TableName);
            Assert.Equal("thread_id", match.ColumnName);
            Assert.Contains("thread-2", match.DisplayText);
            Assert.Equal("Codex home", match.SourceDescription);
            Assert.Equal("codex-home", match.DiscoveryKind);
            Assert.Equal(Path.GetFullPath(secondPath), match.SourceCandidate!.Path);
            Assert.NotEqual(default, match.InspectionCapturedAtUtc);
            Assert.DoesNotContain(trace.Matches, candidate => candidate.TableName == "threads");

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = secondPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand insert = connection.CreateCommand();
                insert.CommandText =
                    "CREATE TABLE repeated(thread_id TEXT); INSERT INTO repeated VALUES ('same'), ('same'), ('same'); CREATE TABLE case_insensitive(thread_id TEXT COLLATE NOCASE); INSERT INTO case_insensitive VALUES ('mixed');";
                await insert.ExecuteNonQueryAsync();
            }

            CodexStateKeyTraceResult capped = await explorer.TraceKeyAsync([secondPath], "thread_id", "same", 2);
            Assert.Equal(2, capped.Matches.Count);
            Assert.True(capped.MayBeTruncated);

            CodexStateKeyTraceResult exactlyCapped = await explorer.TraceKeyAsync([secondPath], "thread_id", "same", 3);
            Assert.Equal(3, exactlyCapped.Matches.Count);
            Assert.False(exactlyCapped.MayBeTruncated);

            CodexStateKeyTraceResult exact = await explorer.TraceKeyAsync([secondPath], "thread_id", "SAME");
            Assert.Empty(exact.Matches);
            Assert.False(exact.MayBeTruncated);

            CodexStateKeyTraceResult caseSensitive = await explorer.TraceKeyAsync([secondPath], "thread_id", "MIXED");
            Assert.DoesNotContain(caseSensitive.Matches, candidate => candidate.TableName == "case_insensitive");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public void DiscoverCandidates_EnumeratesAllRootSqliteStoresWithLocationProvenance()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-root-discovery-");
        try
        {
            DirectoryInfo sqliteDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "sqlite"));
            File.WriteAllBytes(Path.Combine(directory.FullName, "state_5.sqlite"), []);
            File.WriteAllBytes(Path.Combine(directory.FullName, "logs_2.sqlite"), []);
            File.WriteAllBytes(Path.Combine(directory.FullName, "goals_1.db"), []);
            File.WriteAllBytes(Path.Combine(sqliteDirectory.FullName, "codex-dev.db"), []);

            IReadOnlyList<CodexStateDatabaseCandidate>
                candidates = new CodexStateDbExplorerService(directory.FullName).DiscoverCandidates();

            Assert.Equal(4, candidates.Count);
            Assert.All(
                candidates.Where(candidate => candidate.SourceDescription == "Codex home"),
                candidate => Assert.Equal("codex-home", candidate.DiscoveryKind, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                "codex-sqlite-folder",
                Assert.Single(candidates, candidate => candidate.FileName == "codex-dev.db").DiscoveryKind,
                true);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task InspectDiscoveredAsync_PreservesUnavailableInvalidSourcesAndInspectsOthers()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-batch-");
        try
        {
            string validPath = Path.Combine(directory.FullName, "state_5.sqlite");
            string invalidPath = Path.Combine(directory.FullName, "logs_2.sqlite");
            await CreateDatabaseAsync(validPath);
            await File.WriteAllTextAsync(invalidPath, "not a sqlite database");

            CodexStateMultiInspectionResult result = await new CodexStateDbExplorerService(directory.FullName)
                .InspectDiscoveredAsync();

            CodexStateSourceInspection valid = Assert.Single(result.AvailableSources, source => source.Database.Path == validPath);
            Assert.Equal(validPath, valid.Inspection!.Database.Path);
            CodexStateSourceInspection unavailable = Assert.Single(
                result.UnavailableSources,
                source => source.Database.Path == invalidPath);
            Assert.False(string.IsNullOrWhiteSpace(unavailable.Error));
            Assert.Equal("unavailable", unavailable.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SchemaFingerprint_IsStableAndChangesForSchemaObjectsNotRows()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-fingerprint-");
        try
        {
            string firstPath = Path.Combine(directory.FullName, "state_5.sqlite");
            DirectoryInfo secondDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "other-source"));
            string secondPath = Path.Combine(secondDirectory.FullName, "state_5.sqlite");
            await CreateDatabaseAsync(firstPath);
            File.Copy(firstPath, secondPath);
            var explorer = new CodexStateDbExplorerService(directory.FullName);

            CodexStateInspectionResult first = await explorer.InspectAsync(firstPath, false);
            CodexStateInspectionResult repeat = await explorer.InspectAsync(firstPath, false);
            Assert.Equal(first.SchemaFingerprint, repeat.SchemaFingerprint);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = secondPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand alter = connection.CreateCommand();
                alter.CommandText = "CREATE TRIGGER threads_after_insert AFTER INSERT ON threads BEGIN SELECT 1; END;";
                await alter.ExecuteNonQueryAsync();
            }

            CodexStateInspectionResult second = await explorer.InspectAsync(secondPath, false);
            Assert.NotEqual(first.SchemaFingerprint, second.SchemaFingerprint);
            Assert.Contains(second.SchemaObjects, schemaObject => schemaObject.ObjectType == "trigger");

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = firstPath }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO threads(id, model) VALUES ('thread-3', 'gpt-c');";
                await insert.ExecuteNonQueryAsync();
            }

            CodexStateInspectionResult afterRowChange = await explorer.InspectAsync(firstPath, false);
            Assert.Equal(first.SchemaFingerprint, afterRowChange.SchemaFingerprint);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SchemaInspection_RetainsGeneratedColumnsAndIndexDefinitions()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("tajstokens-db-schema-detail-");
        try
        {
            string path = Path.Combine(directory.FullName, "state_5.sqlite");
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                await connection.OpenAsync();
                SqliteCommand schema = connection.CreateCommand();
                schema.CommandText =
                    "CREATE TABLE values_table(raw TEXT, generated TEXT GENERATED ALWAYS AS (upper(raw)) STORED); CREATE INDEX values_index ON values_table(upper(raw));";
                await schema.ExecuteNonQueryAsync();
            }

            CodexStateInspectionResult inspection = await new CodexStateDbExplorerService(directory.FullName)
                .InspectAsync(path, false);
            CodexStateTableInfo table = Assert.Single(inspection.Tables, candidate => candidate.Name == "values_table");
            Assert.Contains(table.Columns, column => column.Name == "generated" && column.IsHidden);
            Assert.Contains(table.Indexes, index => index.Name == "values_index" && index.Sql is not null);
            Assert.Contains(inspection.SchemaObjects, schemaObject => schemaObject.ObjectType == "index");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
                             CREATE TABLE threads(id TEXT PRIMARY KEY, model TEXT, prompt TEXT, rollout_path TEXT, payload BLOB);
                             CREATE INDEX idx_threads_model ON threads(model);
                             INSERT INTO threads(id, model, prompt, rollout_path, payload) VALUES
                                 ('thread-1', 'gpt-a', 'prompt one', 'C:/rollout-1.jsonl', X'0102'),
                                 ('thread-2', 'gpt-b', 'prompt two', 'C:/rollout-2.jsonl', X'0304');
                             CREATE TABLE thread_spawn_edges(parent_thread_id TEXT, child_thread_id TEXT, status TEXT);
                             """;
        await schema.ExecuteNonQueryAsync();
    }
}