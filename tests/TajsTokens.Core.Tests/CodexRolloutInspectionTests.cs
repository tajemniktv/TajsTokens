using Microsoft.Data.Sqlite;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CodexRolloutInspectionTests : IDisposable
{
    private const string ThreadId = "11111111-1111-4111-8111-111111111111";
    private const string ParentId = "22222222-2222-4222-8222-222222222222";
    private readonly string _directory;
    private readonly string _alternate;
    private readonly string _indexed;

    public CodexRolloutInspectionTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root is required for project-local test files.");
        _directory = Path.Combine(root.FullName, ".codex", "temp", "rollout-inspection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _alternate = Path.Combine(_directory, "alternate-" + ThreadId + ".jsonl");
        _indexed = Path.Combine(_directory, "indexed-" + ThreadId + ".jsonl");
    }

    [Fact]
    public void ExternalSqliteHomeUsesGlobalConfigBeforeEnvironmentWithoutMovingRolloutRoots()
    {
        var external = Path.Combine(_directory, "external");
        Assert.Equal(external, CodexSqliteHome.Resolve(_directory, external));
        File.WriteAllText(Path.Combine(_directory, "config.toml"), "sqlite_home = 'configured'\n[profiles.other]\nsqlite_home = 'not-selected'\n");
        Assert.Equal(Path.Combine(_directory, "configured"), CodexSqliteHome.Resolve(_directory, external));
        File.WriteAllText(Path.Combine(_directory, "config.toml"), "sqlite_home = [invalid");
        Assert.Null(CodexSqliteHome.Resolve(_directory, external));
    }

    [Fact]
    public async Task ParserSupportedOwnerVariantsAndBlankLinesRemainComparable()
    {
        var metadata = $"{{\"type\":\"SESSION_META\",\"session_id\":\"{ThreadId}\"}}\n";
        await WritePairAsync(" \t\r\n" + metadata + Event(1), metadata + Event(1));
        var result = await CompareAsync();
        Assert.True(result.OwnershipEstablished);
        Assert.Equal(CodexRolloutComparisonKind.IdenticalOwnedRecords, result.Kind);
        Assert.Equal(2, result.CommonOwnedRecords);
    }

    [Fact]
    public async Task SameCompleteBytes_AreIdenticalWithoutChangingFiles()
    {
        var content = Meta(ThreadId) + Event(1);
        await WritePairAsync(content, content);
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.IdenticalBytes, result.Kind);
        Assert.True(result.OwnershipEstablished);
        Assert.Equal(2, result.CommonOwnedRecords);
        Assert.Equal(content, await File.ReadAllTextAsync(_alternate));
        Assert.Equal(content, await File.ReadAllTextAsync(_indexed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactOwnedPrefix_ReportsOverlapInEitherDirection(bool reverse)
    {
        var first = Meta(ThreadId) + Event(1);
        var second = first + Event(2);
        await WritePairAsync(reverse ? second : first, reverse ? first : second);
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.PrefixOverlap, result.Kind);
        Assert.Equal(2, result.CommonOwnedRecords);
    }

    [Fact]
    public async Task DifferentRecords_AreNotMergedBasedOnSameThread()
    {
        await WritePairAsync(Meta(ThreadId) + Event(1), Meta(ThreadId) + Event(2));
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.DifferentRecords, result.Kind);
        Assert.Equal(1, result.CommonOwnedRecords);
    }

    [Fact]
    public async Task NonOwningPrefix_IsExcludedFromOwnedComparisonButNotWholeByteEquality()
    {
        var owned = Meta(ThreadId) + Event(2);
        await WritePairAsync(Meta(ParentId) + Event(1) + owned, owned);
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.IdenticalOwnedRecords, result.Kind);
        Assert.True(result.HasNonOwningPrefix);
        Assert.True(result.OwnershipEstablished);
        Assert.Equal(2, result.CommonOwnedRecords);
    }

    [Fact]
    public async Task ExactBytes_DoNotEstablishOwnerForNoUuidFilename()
    {
        var path = Path.Combine(_directory, "no-uuid.jsonl");
        await File.WriteAllTextAsync(path, Meta(ThreadId));
        await File.WriteAllTextAsync(_indexed, Meta(ThreadId));
        var result = await new CodexRolloutComparisonReader().CompareAsync(path, _indexed, ThreadId, CancellationToken.None);
        Assert.Equal(CodexRolloutComparisonKind.IdenticalBytes, result.Kind);
        Assert.False(result.OwnershipEstablished);
        Assert.Null(result.CommonOwnedRecords);
    }

    [Fact]
    public async Task UnknownRecordsBeforeOwner_AreNotCountedAsOwned()
    {
        await WritePairAsync(Event(0) + Meta(ThreadId), Meta(ThreadId));
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.IdenticalOwnedRecords, result.Kind);
        Assert.True(result.HasNonOwningPrefix);
        Assert.Equal(1, result.CommonOwnedRecords);
    }

    [Fact]
    public async Task MissingCatalog_IsUnknownRatherThanZeroUnindexedFiles()
    {
        var service = new CodexObservatoryService(null!, null!, new CodexStateCatalog(_directory), null, [_directory]);
        var result = await service.InspectAlternateRolloutsAsync(0, CancellationToken.None);
        Assert.Null(result.UnindexedPaths);
        Assert.Null(result.StateDatabasePath);
        Assert.Empty(result.Comparisons);
        Assert.False(result.HasMore);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("conflicting")]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("empty")]
    [InlineData("wrong-owner")]
    [InlineData("duplicate-owner")]
    [InlineData("conflicting-aliases")]
    [InlineData("matching-aliases")]
    [InlineData("malformed-alias")]
    public async Task InvalidOrAmbiguousInputs_RemainUnresolved(string variant)
    {
        var alternate = variant switch
        {
            "missing" => "{\"type\":\"session_meta\",\"payload\":{}}\n",
            "conflicting" => Meta(ThreadId) + Meta(ParentId),
            "malformed" => Meta(ThreadId) + "{broken}\n",
            "truncated" => Meta(ThreadId) + "{\"type\":\"event_msg\"}",
            "empty" => "",
            "duplicate-owner" => $$$"""{"type":"session_meta","payload":{"id":"{{{ParentId}}}","id":"{{{ThreadId}}}"}}""" + "\n",
            "conflicting-aliases" => $$$"""{"type":"session_meta","payload":{"id":"{{{ThreadId}}}","session_id":"{{{ParentId}}}"}}""" + "\n",
            "matching-aliases" => $$$"""{"type":"session_meta","id":"{{{ThreadId}}}","session_id":"{{{ThreadId}}}"}""" + "\n",
            "malformed-alias" => $$$"""{"type":"session_meta","payload":{"id":null,"session_id":"{{{ThreadId}}}"}}""" + "\n",
            _ => Meta(ParentId)
        };
        await WritePairAsync(alternate, Meta(ThreadId));
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, (await CompareAsync()).Kind);
    }

    [Fact]
    public async Task OversizedFile_IsNotComparedEvenWhenSizesMatch()
    {
        var content = new string(' ', CodexRolloutComparisonReader.MaximumFileBytes + 1);
        await WritePairAsync(content, content);
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, result.Kind);
        Assert.Contains("limit", result.Detail);
        Assert.Null(result.CommonOwnedRecords);
    }

    [Fact]
    public async Task RecordLimit_IsExplicitlyUnresolved()
    {
        var content = Meta(ThreadId) + string.Concat(Enumerable.Repeat("{}\n", 20_000));
        await WritePairAsync(content, content);
        var result = await CompareAsync();
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, result.Kind);
        Assert.Contains("record limit", result.Detail);
    }

    [Fact]
    public async Task ReplacementDuringRead_RejectsEvenSameBytesAndWriteTime()
    {
        await WritePairAsync(Meta(ThreadId), Meta(ThreadId));
        var replacement = Path.Combine(_directory, "replacement.jsonl");
        await File.WriteAllTextAsync(replacement, Meta(ThreadId));
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(_indexed));
        var reader = new CodexRolloutComparisonReader(() => File.Move(replacement, _indexed, overwrite: true));
        var result = await reader.CompareAsync(_alternate, _indexed, ThreadId, CancellationToken.None);
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, result.Kind);
        Assert.Contains("replaced", result.Detail);
    }

    [Fact]
    public async Task UnreadableFile_RemainsUnresolved()
    {
        await WritePairAsync(Meta(ThreadId), Meta(ThreadId));
        using var held = new FileStream(_indexed, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, (await CompareAsync()).Kind);
    }

    [Fact]
    public async Task MissingFile_RemainsUnresolved()
    {
        await File.WriteAllTextAsync(_alternate, Meta(ThreadId));
        Assert.Equal(CodexRolloutComparisonKind.Unresolved, (await CompareAsync()).Kind);
    }

    [Fact]
    public async Task Cancellation_DoesNotProduceAcceptedComparison()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CodexRolloutComparisonReader().CompareAsync(_alternate, _indexed, ThreadId, cancellation.Token));
    }

    [Fact]
    public async Task Inspection_IsPagedAndUsesIndexedCounterpartOutsideRootsWithoutWritingOwnedState()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "sessions")).FullName;
        await File.WriteAllTextAsync(_indexed, Meta(ThreadId));
        for (var i = 0; i < 9; i++)
            await File.WriteAllTextAsync(Path.Combine(root, $"alternate-{i}-{ThreadId}.jsonl"), Meta(ThreadId));
        var statePath = Path.Combine(_directory, "state_5.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={statePath};Pooling=False"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT, created_at INTEGER,
                    updated_at INTEGER, created_at_ms INTEGER, updated_at_ms INTEGER,
                    tokens_used INTEGER, model TEXT, reasoning_effort TEXT, archived INTEGER);
                INSERT INTO threads VALUES($id, $path, 1, 1, 1000, 1000, 0, NULL, NULL, 0);
                """;
            command.Parameters.AddWithValue("$id", ThreadId);
            command.Parameters.AddWithValue("$path", _indexed);
            await command.ExecuteNonQueryAsync();
        }
        var before = await File.ReadAllBytesAsync(statePath);
        // Null writers are deliberate: this read-only capability must never touch any writer.
        var service = new CodexObservatoryService(null!, null!, new CodexStateCatalog(_directory), null, [root]);
        var first = await service.InspectAlternateRolloutsAsync(0, CancellationToken.None);
        Assert.Equal(9, first.UnindexedPaths);
        Assert.Equal(8, first.Comparisons.Count);
        Assert.True(first.HasMore);
        Assert.All(first.Comparisons, item => Assert.Equal(CodexRolloutComparisonKind.IdenticalBytes, item.Kind));
        var last = await service.InspectAlternateRolloutsAsync(8, CancellationToken.None);
        Assert.Single(last.Comparisons);
        Assert.False(last.HasMore);
        Assert.Equal(before, await File.ReadAllBytesAsync(statePath));
    }

    private async Task WritePairAsync(string alternate, string indexed)
    {
        await File.WriteAllTextAsync(_alternate, alternate);
        await File.WriteAllTextAsync(_indexed, indexed);
    }

    private Task<CodexRolloutComparison> CompareAsync() =>
        new CodexRolloutComparisonReader().CompareAsync(_alternate, _indexed, ThreadId, CancellationToken.None);

    private static string Meta(string id) => $$$"""{"type":"session_meta","payload":{"id":"{{{id}}}"}}""" + "\n";
    private static string Event(int ordinal) => $$$"""{"type":"event_msg","payload":{"ordinal":{{{ordinal}}}}}""" + "\n";

    public void Dispose()
    {
        // Only release this fixture's catalog pool; pure byte tests must not disturb unrelated
        // SQLite tests running concurrently in the same process.
        using var catalog = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "state_5.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        SqliteConnection.ClearPool(catalog);
        Directory.Delete(_directory, recursive: true);
    }
}
