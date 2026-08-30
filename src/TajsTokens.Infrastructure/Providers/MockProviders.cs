using System.Text.Json;
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
            new("gpt-5-codex", "repo:TajsTokens", now.AddMinutes(-30), new TokenBreakdown(21500, 4300, 9800, 1250)),
            new("gpt-5-mini", "repo:TajsTokens", now.AddMinutes(-15), new TokenBreakdown(7200, 1800, 3900, 420))
        };

        return Task.FromResult<IReadOnlyList<TokenUsage>>(usage);
    }
}

public sealed class TokscaleJsonAdapter
{
    public IReadOnlyList<TokenUsage> ParseUsage(string json, DateTimeOffset observedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Tokscale JSON contract is not yet verified. Expected top-level 'entries' array.");
        }

        var usages = new List<TokenUsage>();
        foreach (var entry in entries.EnumerateArray())
        {
            var model = entry.GetProperty("model").GetString() ?? "unknown";
            var scope = entry.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() ?? "global" : "global";

            var breakdown = new TokenBreakdown(
                entry.GetProperty("input").GetDouble(),
                entry.TryGetProperty("cached_input", out var cachedInput) ? cachedInput.GetDouble() : 0,
                entry.GetProperty("output").GetDouble(),
                entry.TryGetProperty("reasoning", out var reasoning) ? reasoning.GetDouble() : 0);

            usages.Add(new TokenUsage(model, scope, observedAtUtc, breakdown));
        }

        return usages;
    }
}

public sealed class StubCodexQuotaProvider : ICodexQuotaProvider
{
    public Task<IReadOnlyList<QuotaSnapshot>> GetQuotaSnapshotsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new List<QuotaSnapshot>
        {
            new(QuotaWindowKind.FiveHour, now, 54200, 200000, now.AddHours(2.4)),
            new(QuotaWindowKind.Weekly, now, 1320000, 5000000, now.AddDays(4.1))
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
