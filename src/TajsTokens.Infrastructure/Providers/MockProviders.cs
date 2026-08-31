using TajsTokens.Core.Enums;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Providers;

public sealed class MockTokscaleProvider : ITokscaleProvider
{
    public Task<IReadOnlyList<TokenUsage>> GetUsageObservationsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var usage = new List<TokenUsage>
        {
            new(
                "tokscale",
                "codex",
                "gpt-5-codex",
                now.AddMinutes(-30),
                new TokenBreakdown(21_500, 43_000, 0, 8_550, 1_250, 74_300),
                Profile: "default",
                Repository: "TajsTokens"),
            new(
                "tokscale",
                "codex",
                "gpt-5-mini",
                now.AddMinutes(-15),
                new TokenBreakdown(7_200, 18_000, 0, 3_480, 420, 29_100),
                Profile: "default",
                Repository: "TajsTokens")
        };

        return Task.FromResult<IReadOnlyList<TokenUsage>>(usage);
    }

    public Task<IReadOnlyList<TokenTimeBucket>> GetHourlyUsageAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TokenTimeBucket> buckets =
        [
            new("09:00", null, new TokenBreakdown(3_000, 7_000, 0, 800, 200, 11_000)),
            new("10:00", null, new TokenBreakdown(4_000, 9_000, 0, 900, 250, 14_150))
        ];
        return Task.FromResult(buckets);
    }
}

public sealed class StubCodexQuotaProvider : ICodexQuotaProvider
{
    public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<QuotaSnapshot>
        {
            new(QuotaWindowKind.FiveHour, now, 27.0, 300, now.AddHours(2.4), "codex", "default", "mock"),
            new(QuotaWindowKind.Weekly, now, 36.0, 10_080, now.AddDays(4.1), "codex", "default", "mock")
        };

        return Task.FromResult<IReadOnlyList<QuotaSnapshot>>(snapshots);
    }
}

public sealed class StubAnnouncementProvider : IAnnouncementProvider
{
    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(CancellationToken cancellationToken)
    {
        var announcements = new List<Announcement>
        {
            new(
                "mock-announcement-1",
                DateTimeOffset.UtcNow.AddHours(-3),
                "MockFeed",
                "Quota reset policy update (placeholder)",
                "Announcement provider is currently stubbed until source contracts are finalized.",
                null)
        };

        return Task.FromResult<IReadOnlyList<Announcement>>(announcements);
    }
}
