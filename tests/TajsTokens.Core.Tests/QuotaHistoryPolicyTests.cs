using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Ingestion;

namespace TajsTokens.Core.Tests;

public sealed class QuotaHistoryPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PairedRoundedAndUnpairedSourcesRemainIndependentUnknownAccountCohorts()
    {
        var rollout = Point(0, 12.4);
        var app = rollout with { Source = "codex-app-server:codex", AccountKey = "known", UsedPercent = 12,
            SourceIdentity = null, SessionId = null };
        var decisions = QuotaHistoryPolicy.Describe([rollout, app, Point(1, 14.2)], Start.AddHours(2));
        Assert.All(decisions, x => Assert.True(x.Eligible));
        Assert.Equal(2, QuotaHistoryPolicy.Streams(decisions).Count());
        Assert.Null(decisions.Single(x => x.Observation == rollout).Cohort.AccountKey);
        Assert.Empty(QuotaPredictionService.Predict(Data([rollout, Point(1, 14.2)]), Point(1, 14.2), Start.AddHours(1)));
    }

    [Fact]
    public void CachedRepeatsAreNotZeroBurnTargetsAndConflictBreaksSlopes()
    {
        var decisions = QuotaHistoryPolicy.Describe([
            Point(0, 10), Point(0.5, 10), Point(1, 12),
            Point(1.5, 13), Point(1.5, 15) with { ObservationId = "alternative" }, Point(2, 16), Point(2.5, 18)], Start.AddHours(3));
        Assert.Single(decisions, x => x.Reason == "possibly-cached-repeat");
        Assert.Equal(2, decisions.Count(x => x.Reason == "same-time-conflict"));
        var epochs = QuotaForecastCalibration.SplitEpochs(QuotaHistoryPolicy.ReplayRows(Assert.Single(QuotaHistoryPolicy.Streams(decisions))));
        Assert.Equal(2, epochs.Count);
        Assert.Equal(new[] { 2, 2 }, epochs.Select(x => x.Count));
        Assert.Equal(Start.AddHours(2), epochs[1][0].CapturedAtUtc);
    }

    [Fact]
    public void AccountBucketPlanWindowAndUnknownSessionsDoNotPool()
    {
        var row = Point(0, 10);
        var rows = new[] { row, row with { AccountKey = "A" }, row with { LimitId = "other" },
            row with { PlanType = "other" }, row with { WindowMinutes = 600 }, row with { SessionId = "other" } };
        Assert.Equal(6, QuotaHistoryPolicy.Streams(QuotaHistoryPolicy.Describe(rows, Start)).Count());
    }

    [Fact]
    public void LegacyMissingInvalidAndFutureEvidenceHasExplicitReasons()
    {
        var row = Point(0, 10);
        var variants = new[] { row with { ObservationId = null }, row with { HasSourceTimestamp = false },
            row with { ResetsAtUtc = Start.AddSeconds(-1) }, row with { UsedPercent = double.NaN },
            row with { CapturedAtUtc = Start.AddDays(1) }, row with { Kind = QuotaWindowKind.Unknown } };
        var reasons = variants.Select(x => Assert.Single(QuotaHistoryPolicy.Describe([x], Start))).ToArray();
        Assert.All(reasons, x => Assert.False(x.Eligible));
        Assert.Equal(new[] { "legacy-missing-provenance", "missing-event-time", "invalid-window", "invalid-window", "future-event", "unsupported-window" }, reasons.Select(x => x.Reason));
    }

    [Fact]
    public void CollectionReplayCannotUseBackfilledQuotaAtHistoricalOrigins()
    {
        var rows = new[] { Point(0, 10), Point(0.5, 12), Point(1, 14), Point(1.5, 16) }
            .Select(x => x with { CollectedAtUtc = Start.AddDays(1) }).ToArray();
        Assert.NotEmpty(QuotaForecastCalibration.Replay(rows, "persistence", 0.5));
        var strict = QuotaHistoryPolicy.AvailableRows(rows, ForecastReplayAvailability.CollectedByOrigin);
        Assert.Empty(QuotaForecastCalibration.Replay(strict, "persistence", 0.5));
        Assert.Equal(Start, rows[0].CapturedAtUtc); // Read policy never rewrites durable event time.
    }

    [Fact]
    public void ParserRetainsNativeQuotaIdentityPrecisionPlanAndUnknownWindow()
    {
        const string session = "11111111-1111-4111-8111-111111111111";
        var file = $"rollout-{session}.jsonl";
        var state = new RolloutParseState(file, "generation");
        var parser = new CodexRolloutParser();
        parser.Parse(new RawSessionRecord(file, 0, 100,
            $$$"""{"timestamp":"2026-09-01T10:00:00Z","type":"session_meta","payload":{"id":"{{{session}}}"}}"""), state);
        var record = new RawSessionRecord(file, 100, 500,
            """{"timestamp":"2026-09-01T10:01:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex-extra","plan_type":"pro","primary":{"used_percent":12.375,"window_minutes":300,"resets_at":1788260400},"secondary":{"used_percent":4,"window_minutes":60,"resets_at":1788256800}}}}""");
        var parsed = parser.Parse(record, state).QuotaSnapshots;
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, x =>
        {
            Assert.Equal("generation", x.SourceIdentity); Assert.Equal(session, x.SessionId);
            Assert.Equal("codex-extra", x.LimitId); Assert.Equal("pro", x.PlanType);
            Assert.Null(x.AccountKey); Assert.NotNull(x.CollectedAtUtc); Assert.NotNull(x.ObservationId);
        });
        Assert.Equal(12.375, parsed[0].UsedPercent);
        Assert.Equal(QuotaWindowKind.Unknown, parsed[1].Kind);
        Assert.Equal(parsed.Select(x => x.ObservationId), parser.Parse(record, state).QuotaSnapshots.Select(x => x.ObservationId));
    }

    private static QuotaSnapshot Point(double hours, double used) => new(QuotaWindowKind.FiveHour,
        Start.AddHours(hours), used, 300, Start.AddHours(5), "codex", "default", "codex-rollout:primary")
    {
        ObservationId = "record:" + hours, SourceIdentity = "generation", SessionId = "session",
        LimitId = "codex", PlanType = "pro", Lane = "primary", CollectedAtUtc = Start.AddHours(hours), HasSourceTimestamp = true
    };
    private static CodexForecastDataset Data(QuotaSnapshot[] rows) => new(rows, [], [], [], Start.AddHours(2), "fixture");
}
