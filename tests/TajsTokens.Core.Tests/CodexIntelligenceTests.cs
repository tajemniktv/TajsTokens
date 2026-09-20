// Taj's Tokens | CodexIntelligenceTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Text.Json;
using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Infrastructure.Services;

#endregion

namespace TajsTokens.Core.Tests;

public sealed class CodexIntelligenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

    [Fact]
    public async Task LedgerUsesNativeThreadIdentityAndImmutableCapturesWithoutInventingAccountOwnership()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx"))) root = root.Parent;
        string directory = Path.Combine(root!.FullName, ".codex", "temp", "intelligence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "telemetry.db");
            var repository = new SqliteTelemetryRepository(database);
            await repository.InitializeAsync(default);
            using var store = new SqliteCodexObservatoryStore(database);
            await store.InitializeAsync(default);
            using (var connection = new SqliteConnection("Data Source=" + database))
            {
                await connection.OpenAsync();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                                      INSERT INTO sessions(session_id,thread_id,repository,started_at_utc,status) VALUES('session','native-thread','project',$at,'complete');
                                      INSERT INTO rollout_files(source_identity,file_path,session_id,size_bytes,last_seen_at_utc) VALUES('source','file','session',100,$at);
                                      INSERT INTO codex_native_token_events(source_event_id,source_file,session_id,observed_at_utc,captured_at_utc,model,counter_epoch,
                                        uncached_input_tokens,cache_read_tokens,cache_write_tokens,non_reasoning_output_tokens,reasoning_output_tokens,reported_total_tokens)
                                        VALUES('event','file','session',$at,$at,'gpt-6-astra',0,10,20,0,3,2,35);
                                      """;
                command.Parameters.AddWithValue("$at", Start.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            var capture = new CodexServerObservation(
                "task",
                CodexServerSurface.TaskUsage,
                null,
                Start,
                Start,
                CodexServerEvidenceParser.Contract,
                CodexHistoricalAnalyticsParser.Contract,
                ServerEvidenceState.Available,
                "fixture")
            {
                CorrelatedAccountKey = "account",
                TaskUsage = new CodexTaskUsageReport(
                    Start,
                    ["native-thread"],
                    [new CodexTaskUsage("native-thread", "partial", "task", new CodexTaskAmounts(null, 2.24m, 0), [])]),
            };
            await repository.SaveServerEvidenceAsync(new CodexServerCollection([capture]), default);
            await repository.SaveServerEvidenceAsync(new CodexServerCollection([capture]), default);
            await repository.SaveServerEvidenceAsync(
                new CodexServerCollection(
                [
                    capture with
                    {
                        Id = "activity",
                        Surface = CodexServerSurface.AccountActivity,
                        TaskUsage = null,
                        Activity = new CodexAccountActivity(35, 35, null, null, null, [new CodexAccountDay("2026-09-01", 35)]),
                    },
                ]),
                default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveServerEvidenceAsync(
                new CodexServerCollection([capture with { Detail = "overwrite" }]),
                default));
            var service = new SqliteIntelligenceService(database, repository);
            var evidence = new CodexServerEvidenceService(database, repository, new CodexAppServerEvidenceProvider());
            var engine = new CodexIntelligenceEngine(
                () => new SqliteForecastDatasetReader(database),
                evidence,
                service,
                () => TelemetrySnapshot.Empty);
            var selection = new CodexSelection(Start, Start.AddDays(1));
            CodexIntelligenceSnapshot result = await engine.QueryAsync(selection, default);
            Assert.Equal(
                35,
                Assert.Single(Assert.Single(result.ProviderEvidence, x => x.Activity is not null).Activity!.DailyUsageBuckets!).Tokens);
            Assert.Equal("native-thread", Assert.Single(result.Ledger).ThreadId);
            Assert.Equal("source", result.Ledger[0].SourceIdentity);
            Assert.Equal(35, Assert.Single(result.Chats).SelectedLocalTokens);
            Assert.Contains("ownership incomplete", result.Chats[0].Compatibility);
            Assert.Empty((await engine.QueryAsync(selection with { AccountKey = "account" }, default)).Ledger);
            CodexForecastDataset canonical = await new SqliteForecastDatasetReader(
                    database,
                    [
                        new RolloutAccountAssociation(
                            "wrong-provider",
                            "other",
                            "default",
                            "source",
                            "session",
                            Start,
                            Start,
                            "account",
                            Start),
                    ])
                .ReadAsync("codex", "default", Start, Start.AddDays(1), default, includeLedger: true);
            Assert.Equal(Assert.Single(canonical.Tokens), Assert.Single(canonical.Ledger).Workload);
            Assert.Equal(AccountEvidenceClass.Unattributed, canonical.Ledger[0].Ownership);
            var asserted = new CodexIntelligenceEngine(
                () => new SqliteForecastDatasetReader(
                    database,
                    [
                        new RolloutAccountAssociation(
                            "claim",
                            "codex",
                            "default",
                            "source",
                            "session",
                            Start,
                            Start,
                            "account",
                            Start.AddDays(1)),
                    ]),
                evidence,
                service,
                () => TelemetrySnapshot.Empty);
            CodexIntelligenceSnapshot attributed = await asserted.QueryAsync(
                selection with { AccountKey = "account", ThreadId = "native-thread" },
                default);
            Assert.Single(attributed.Ledger);
            Assert.Single(attributed.Chats);
            Assert.Empty(attributed.Current);
            Assert.Empty(attributed.QuotaTimeline);
            await repository.SaveServerEvidenceAsync(
                new CodexServerCollection(
                [
                    capture with
                    {
                        Id = "failed",
                        CollectedAtUtc = Start.AddHours(1),
                        CorrelatedAccountKey = null,
                        TaskUsage = null,
                        State = ServerEvidenceState.Error,
                    },
                ]),
                default);
            CodexIntelligenceSnapshot failed = await asserted.QueryAsync(selection with { AccountKey = "account" }, default);
            Assert.Empty(failed.Chats);
            Assert.Contains(failed.ProviderEvidence, x => x.Id == "failed");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LiveProjectionPreservesMetersWithoutForecastAndNumericArtifactsRoundTrip()
    {
        var quota = new QuotaSnapshot(QuotaWindowKind.Weekly, Start, 42, 10080, Start.AddDays(7), "codex", "default", "provider");
        TelemetrySnapshot telemetry = TelemetrySnapshot.Empty with
        {
            CapturedAtUtc = Start, QuotaSnapshots = [quota], QuotaDataFresh = true,
        };
        CodexIntelligenceSnapshot live = CodexIntelligenceProjection.Live(telemetry);
        Assert.Equal(quota, Assert.Single(live.Current).Current);
        Assert.Null(live.Current[0].Forecast);
        Assert.Equal(SystemTrayStatusPresenter.Build(telemetry), SystemTrayStatusPresenter.Build(live));
        Assert.DoesNotContain(typeof(CodexIntelligenceSnapshot).GetProperties(), p => p.PropertyType == typeof(TelemetrySnapshot));
        var artifact = new CodexNumericInference("fixture", 50, [2, 3], [-1, -2], 0, 100);
        Assert.Equal(42, JsonSerializer.Deserialize<CodexNumericInference>(JsonSerializer.Serialize(artifact))!.Reconstruct());
        Assert.Equal(artifact, JsonSerializer.Deserialize<CodexNumericInference>(JsonSerializer.Serialize(artifact)));
    }

    [Fact]
    public void SharedSelectionDoesNotAttributeUnknownWorkAndAllBreakdownsReconcile()
    {
        CodexLedgerEntry Row(string id, string? account, string model, string project, DateTimeOffset at)
        {
            return new CodexLedgerEntry(
                id,
                "source",
                id,
                project,
                at,
                at,
                account,
                account is null ? AccountEvidenceClass.Unattributed : AccountEvidenceClass.UserDeclaredSingleAccount,
                new CodexPredictiveTokenEvent(id, at, at, model, "low", 10, 20, 0, 3, 2, 35));
        }

        CodexLedgerEntry[] rows = new[]
        {
            Row("a", "one", "m", "p", Start),
            Row("b", null, "m", "p", Start),
            Row("c", "two", "m", "p", Start),
            Row("d", "one", "other", "p", Start),
            Row("e", "one", "m", "p", Start.AddDays(1)),
        };
        var selection = new CodexSelection(Start, Start.AddDays(1), "one", "m", "p");
        IReadOnlyList<CodexLedgerEntry> selected = CodexIntelligenceProjection.Select(rows, selection);
        Assert.Equal("a", Assert.Single(selected).ThreadId);
        Assert.Equal(35, Assert.Single(CodexIntelligenceProjection.Buckets(selected, true)).Tokens);
        Assert.All(CodexIntelligenceProjection.Groups(selected).GroupBy(x => x.Dimension), g => Assert.Equal(35, g.Sum(x => x.Tokens)));
        Assert.NotEqual(
            CodexIntelligenceProjection.Manifest(selection).Id,
            CodexIntelligenceProjection.Manifest(selection with { AccountKey = "two" }).Id);
        Assert.NotEqual(
            CodexIntelligenceProjection.Manifest(selection, "old-evidence").Id,
            CodexIntelligenceProjection.Manifest(selection, "new-evidence").Id);
    }

    [Fact]
    public void HistoricalContractsPreserveDifferentDenominatorsUnknownsAndUnknownDimensions()
    {
        CodexPlanHistoryReport plan = CodexHistoricalAnalyticsParser.ParsePlan(
            """
            {"periods":[{"id":"p","window_minutes":10080,"plan_type":"pro","starts_at":"2026-09-01T00:00:00Z",
            "ends_at":"2026-09-08T00:00:00Z","used_basis_points":10010,"accounting_complete":false,
            "breakdowns":[{"dimension":"new_dimension","rows":[{"key":"new","basis_points":10010}]}]}]}
            """);
        Assert.True(plan.Approximate);
        Assert.Null(plan.CoverageComplete);
        Assert.Equal(10010m, plan.Periods[0].UsedBasisPoints);
        Assert.Equal("new_dimension", plan.Periods[0].Breakdowns[0].Dimension);
        const string json =
            """{"threads":[{"thread_id":"t","data_status":"partial","usage_source":"task","weekly_limit_percent":2.24,"balance_usage_credits":"0","groups":[]}]}""";
        CodexTaskUsageReport task = CodexHistoricalAnalyticsParser.ParseTasks(json, ["t"]);
        Assert.Equal(2.24m, task.Threads[0].Amounts.WeeklyLimitPercent);
        Assert.Null(task.Threads[0].Amounts.FiveHourLimitPercent);
        Assert.Equal(0m, task.Threads[0].Amounts.BalanceUsageCredits);
        Assert.Throws<InvalidDataException>(() => CodexHistoricalAnalyticsParser.ParseTasks(json, ["other"]));
    }

    [Fact]
    public void ProviderRevisionsAndAlternativePartitionsAreNotSummedOrMadeFinal()
    {
        var period = new CodexPlanPeriod(
            "period",
            10080,
            "pro",
            Start,
            Start.AddDays(7),
            false,
            10000,
            [
                new CodexPlanBreakdown("model", [new CodexPlanValue("a", 10000)]),
                new CodexPlanBreakdown("surface", [new CodexPlanValue("desktop", 10000)]),
            ]);

        CodexServerObservation Row(int i, decimal used)
        {
            return new CodexServerObservation(
                i.ToString(),
                CodexServerSurface.PlanHistory,
                null,
                Start.AddDays(8).AddHours(i),
                Start.AddDays(8).AddHours(i),
                "contract",
                CodexHistoricalAnalyticsParser.Contract,
                ServerEvidenceState.Available,
                "fixture")
            {
                CorrelatedAccountKey = "a",
                AccountEvidence = AccountEvidenceClass.ServerCorrelated,
                PlanHistory = new CodexPlanHistoryReport(Start.AddDays(7), Start, false, true, 0, [period with { UsedBasisPoints = used }]),
            };
        }

        CodexPeriodReconciliation result = Assert.Single(
            CodexProviderReconciliation.Analyze(
                [Row(0, 9900), Row(1, 10000)],
                [],
                [],
                new CodexSelection(Start, Start.AddDays(9)),
                Start.AddDays(9)));
        Assert.Equal(100m, result.ReportedPercent);
        Assert.Equal(1, result.Revisions);
        Assert.Contains("Incomplete", result.States);
        Assert.Contains("Revised", result.States);
        Assert.Null(result.LastCompatibleMeterPercent);
        Assert.DoesNotContain(result.States, x => x.StartsWith("Conflicting partition"));
        Assert.Empty(
            CodexProviderReconciliation.Analyze(
                [Row(0, 9900), Row(1, 10000) with { State = ServerEvidenceState.Error, PlanHistory = null }],
                [],
                [],
                new CodexSelection(Start, Start.AddDays(9)),
                Start.AddDays(9)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetrospectiveWithinCycleShiftDoesNotRequireResetBoundary(bool timeExplainsShift)
    {
        QuotaCostTrial[] trials = Enumerable.Range(0, 48).Select(i => new QuotaCostTrial(
            Start.AddHours(i),
            Start.AddHours(i + 1),
            Start.AddDays(7),
            i < 24 ? 5 : 10,
            i < 24 ? 4.5 : 9.5,
            i < 24 ? 5.5 : 10.5,
            5,
            0,
            0,
            null,
            null)).ToArray();
        var scores = new List<QuotaCostScore> { Score("categories", trials) };
        if (timeExplainsShift) scores.Add(Score("time-ablation", trials.Select(x => x with { Prediction = x.ObservedDelta }).ToArray()));
        CodexRegimeReport result =
            CodexRegimeModel.Analyze(new QuotaCostReport("test", "test", "test", 48, new Dictionary<string, int>(), scores));
        if (timeExplainsShift)
        {
            Assert.Empty(result.Candidates);
        }
        else
        {
            CodexRegimeCandidate candidate = Assert.Single(result.Candidates);
            Assert.InRange(Start.AddHours(24), candidate.BoundaryLowerUtc, candidate.BoundaryUpperUtc);
            Assert.Equal(2, candidate.RelativeRate);
        }
        QuotaCostTrial[] stable = trials.Select(x => x with { ObservedDelta = 5, LowerDelta = 4.5, UpperDelta = 5.5 }).ToArray();
        Assert.Empty(
            CodexRegimeModel.Analyze(
                new QuotaCostReport("test", "test", "test", 48, new Dictionary<string, int>(), [Score("categories", stable)])).Candidates);
    }

    private static QuotaCostScore Score(string candidate, IReadOnlyList<QuotaCostTrial> trials)
    {
        return new QuotaCostScore(
            new QuotaHistoryCohort("codex", "default", QuotaWindowKind.Weekly, "source", "account", "codex", "pro", null, 10080),
            1,
            candidate,
            68,
            20,
            1,
            trials.Count,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            false,
            "evaluation-only",
            new Dictionary<string, double>(),
            trials,
            []);
    }

    [Theory]
    [InlineData("spike")]
    [InlineData("periodic")]
    [InlineData("coarse")]
    public void RetrospectiveDetectorRejectsIsolatedOrAlternatingNoiseAndMeterEnvelopes(string control)
    {
        QuotaCostTrial[] trials = Enumerable.Range(0, 48).Select(i =>
        {
            int value = control == "spike" && i == 24 ? 40 : control == "periodic" ? i % 2 == 0 ? 5 : 15 : 5;
            return new QuotaCostTrial(
                Start.AddHours(i),
                Start.AddHours(i + 1),
                Start.AddDays(7),
                value,
                control == "coarse" ? 0 : value - .5,
                control == "coarse" ? 20 : value + .5,
                5,
                0,
                0,
                null,
                null);
        }).ToArray();
        Assert.Empty(
            CodexRegimeModel.Analyze(
                new QuotaCostReport("test", "test", "test", 48, new Dictionary<string, int>(), [Score("categories", trials)])).Candidates);
    }
}