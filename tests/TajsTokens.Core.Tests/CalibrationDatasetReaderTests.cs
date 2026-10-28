using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Services;

namespace TajsTokens.Core.Tests;

public sealed class CalibrationDatasetReaderTests
{
    [Fact]
    public async Task SharedReadsMatchNarrowSourceReadsIncludingRetiredActivityAndAccountCutoffs()
    {
        using var f = new CalibrationEvidenceTests.Fixture(); await f.InitializeAsync();
        await f.Store.UpsertRolloutFileAsync("valid", "fixture.jsonl", "s", 20, f.Now, default);
        foreach (var row in new[]
        {
            Metadata("normal-meta", "s", "valid", f.Now.AddDays(-10), 0),
            Metadata("ghost-meta", "ghost", "valid", f.Now.AddDays(-10), 1),
            Metadata("retired-recent", "ghost", "retired", f.Now.AddMinutes(-5), 2),
            Metadata("normal-start", "s", "valid", f.Now.AddHours(-2), 3) with { EventType = "task_started", TurnId = "t" },
            Metadata("normal-end", "s", "valid", f.Now.AddMinutes(-30), 4) with
                { EventType = "task_complete", TurnId = "t", CapturedAtUtc = f.Now.AddMinutes(20) },
        }) await f.Store.UpsertWorkloadObservationAsync(row, default);
        await f.AddTokenAsync(f.Now.AddDays(-3));
        await f.AddTokenAsync(f.Now.AddMinutes(-30));
        await f.Store.UpsertContextObservationAsync(new("ctx", "s", null, f.Now.AddMinutes(-30), "m", 80, 100, true,
            CapturedAtUtc: f.Now.AddMinutes(30)), default);
        foreach (var quota in new[]
        {
            f.Quota with { CapturedAtUtc = f.Now.AddDays(-3), AccountKey = "account" },
            f.Quota with { CapturedAtUtc = f.Now.AddHours(-2), AccountKey = "other" },
            f.Quota with { CapturedAtUtc = f.Now.AddMinutes(-20), AccountKey = null },
            f.Quota with { CapturedAtUtc = f.Now.AddMinutes(40), AccountKey = "account" },
        }) await f.Repository.UpsertQuotaSnapshotAsync(quota, default);
        RolloutAccountAssociation[] associations = [new("assertion", "codex", "default", "valid", "s",
            f.Now.AddDays(-3), f.Now.AddDays(-1), "account", f.Now.AddMinutes(20))];
        var source = new SqliteForecastDatasetReader(f.Path, associations);
        var shared = new CalibrationDatasetReader(f.Path, associations, f.Now.AddDays(-5), f.Now.AddHours(1));
        DateTimeOffset readStarted = DateTimeOffset.UtcNow;
        DateTimeOffset? firstCapture = null;
        foreach (var request in new[]
        {
            (From: f.Now.AddDays(-5), To: f.Now.AddHours(1), Account: (string?)"account", Quota: true),
            (From: f.Now.AddDays(-1), To: f.Now, Account: (string?)"account", Quota: true),
            (From: f.Now.AddDays(-1), To: f.Now.AddMinutes(30), Account: (string?)"account", Quota: true),
            (From: f.Now.AddDays(-1), To: f.Now, Account: (string?)"other", Quota: true),
            (From: f.Now.AddDays(-1), To: f.Now, Account: (string?)null, Quota: false),
        })
        {
            var expected = await source.ReadAsync("codex", "default", request.From, request.To, default, request.Account, request.Quota);
            var actual = await shared.ReadAsync("codex", "default", request.From, request.To, default, request.Account, request.Quota);
            Assert.Equal(NumericEvidence(expected), NumericEvidence(actual));
            Assert.InRange(actual.CapturedAtUtc, readStarted, DateTimeOffset.UtcNow);
            firstCapture ??= actual.CapturedAtUtc;
            Assert.Equal(firstCapture, actual.CapturedAtUtc);
            Assert.Contains(actual.Workload, x => x.SourceRecordId == "ghost-meta");
            Assert.DoesNotContain(actual.Workload, x => x.SourceRecordId == "retired-recent");
        }
        Assert.Equal(1, shared.SharedFeatureReads);
        Assert.Equal(5, shared.ReusedReads);
        // A same-row-count correction must invalidate the cache; a derived write must not.
        await f.SqlAsync("UPDATE context_observations SET input_tokens=99;");
        var corrected = await shared.ReadAsync("codex", "default", f.Now.AddDays(-1), f.Now, default, includeQuota: false);
        Assert.Equal(99, Assert.Single(corrected.Context).InputTokens);
        Assert.Equal(2, shared.SharedFeatureReads);
        await f.Repository.UpsertForecastSnapshotAsync(new("codex", "default",
            TajsTokens.Core.Services.QuotaPredictionService.BuildResetOutlook([f.Quota], f.Now)), default);
        var warm = await shared.ReadAsync("codex", "default", f.Now.AddDays(-1), f.Now, default, includeQuota: false);
        Assert.Same(corrected.Tokens[0], warm.Tokens[0]);
        Assert.Equal(2, shared.SharedFeatureReads);
    }

    [Fact]
    public async Task ChangedEvidenceDuringSharedCaptureFallsBackToOneCurrentRead()
    {
        using var f = new CalibrationEvidenceTests.Fixture(); await f.InitializeAsync();
        await f.AddTokenAsync(f.Now.AddMinutes(-1));
        var changing = new ChangingReader(f);
        var shared = new CalibrationDatasetReader(f.Path, [], f.Now.AddDays(-120), f.Now, changing);
        var result = await shared.ReadAsync("codex", "default", f.Now.AddDays(-30), f.Now, default, includeQuota: false);
        Assert.Equal("repaired", Assert.Single(result.Tokens).Model);
        Assert.Equal(0, shared.ReusedReads);
    }

    [Fact]
    public async Task WideBoundFailureFallsBackToSupportedNarrowRead()
    {
        using var f = new CalibrationEvidenceTests.Fixture(); await f.InitializeAsync();
        await f.AddTokenAsync(f.Now.AddMinutes(-1));
        var bounded = new BoundedReader();
        var shared = new CalibrationDatasetReader(f.Path, [], f.Now.AddDays(-120), f.Now, bounded);
        var actual = await shared.ReadAsync("codex", "default", f.Now.AddDays(-30), f.Now, default, includeQuota: false);
        var expected = await new SqliteForecastDatasetReader(f.Path).ReadAsync("codex", "default", f.Now.AddDays(-30), f.Now,
            default, includeQuota: false);
        Assert.Equal(NumericEvidence(expected), NumericEvidence(actual));
        Assert.Single(actual.Tokens);
        await shared.ReadAsync("codex", "default", f.Now.AddDays(-30), f.Now, default, includeQuota: false);
        Assert.Equal(1, bounded.Calls);
        Assert.Equal(0, shared.ReusedReads);
    }

    private static string NumericEvidence(CodexForecastDataset data) =>
        JsonSerializer.Serialize(data with { CapturedAtUtc = DateTimeOffset.MinValue, Coverage = "" });

    private static CodexWorkloadObservation Metadata(string id, string session, string source, DateTimeOffset at, long offset) =>
        new(id, source, "fixture.jsonl", offset, offset + 1, session, "session_meta", at, at,
            null, null, null, "m", "high", null, SessionSourceKind: "cli");

    private sealed class BoundedReader : ICodexForecastDatasetReader
    {
        public int Calls { get; private set; }
        public Task<CodexForecastDataset> ReadAsync(string provider, string profile, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken cancellationToken, string? accountKey = null, bool includeQuota = true, bool includeLedger = false)
        { Calls++; throw new ForecastDatasetReadLimitException("synthetic wider read exceeds its bound"); }
    }

    private sealed class ChangingReader(CalibrationEvidenceTests.Fixture fixture) : ICodexForecastDatasetReader
    {
        public async Task<CodexForecastDataset> ReadAsync(string provider, string profile, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken cancellationToken, string? accountKey = null, bool includeQuota = true, bool includeLedger = false)
        {
            var before = await new SqliteForecastDatasetReader(fixture.Path).ReadAsync(provider, profile, fromUtc, toUtc,
                cancellationToken, accountKey, includeQuota, includeLedger);
            await fixture.SqlAsync("UPDATE codex_native_token_events SET model='repaired';");
            return before;
        }
    }
}
