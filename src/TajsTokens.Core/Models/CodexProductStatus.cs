using TajsTokens.Core.Services;

namespace TajsTokens.Core.Models;

/// <summary>Measured quantities and their scope; freshness never changes a retained numeric amount.</summary>
public sealed record CodexMeasuredWork(decimal? NativeTokens, TtScoreAssessment? Tt, string? BasisId, string? Vintage,
    string Scope, string Source, DateTimeOffset? ReadAtUtc, DateTimeOffset? LastCollectionUtc,
    bool IsFresh, bool IsFallback, string Coverage);

public sealed record CodexStatusChange(string Code, string Summary, string Detail, DateTimeOffset ObservedAtUtc,
    string? AccountKey = null);

public sealed record CodexProductStatus(DateTimeOffset AsOfUtc, AccountOutlook Allowances,
    CodexMeasuredWork Work, IReadOnlyList<CodexStatusChange> Changes)
{
    public TokenWorkloadForecast? LocalNowcast { get; init; }
}
