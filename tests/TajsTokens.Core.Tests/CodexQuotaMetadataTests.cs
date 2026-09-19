using System.Text.Json;
using TajsTokens.Infrastructure.Providers;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Tests;

public sealed class CodexQuotaMetadataTests
{
    [Fact]
    public void MetadataSurvivesNoWindowsAndKeepsNamedAndLegacyViewsSeparate()
    {
        var response = CodexAppServerQuotaProvider.ParseQuotaResponse("""
            {"result":{"accountId":"secret-account","rateLimitsByLimitId":{
              "codex":{"limitId":"codex","limitName":"Codex","planType":"prolite","normalModelSlug":"gpt-test",
                "rateLimitReachedType":"future-reason","spendControlReached":false,
                "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},
                "individualLimit":{"limit":"12.50","used":"0","remainingPercent":100,"resetsAt":1800000000}},
              "reviews":{"limitName":"Reviews","primary":{"usedPercent":130,"windowDurationMins":42,"resetsAt":1800000000}}},
              "rateLimits":{"credits":{"hasCredits":true,"unlimited":true,"balance":null}},"email":"do-not-retain"}}
            """, DateTimeOffset.UtcNow);
        Assert.Empty(response.Snapshots);
        var evidence = response.MetadataObservation!;
        Assert.Equal(AccountEvidenceClass.ProviderVerified, evidence.AccountEvidence);
        Assert.Equal(response.AccountKey, evidence.CorrelatedAccountKey);
        var limits = evidence.QuotaMetadata!.Limits;
        Assert.Equal(3, limits.Count);
        var codex = limits.Single(l => l.ResponseKey == "named:codex");
        Assert.Equal("future-reason", codex.RateLimitReachedType);
        Assert.False(codex.SpendControlReached);
        Assert.Equal("0", codex.Credits!.Balance);
        Assert.False(codex.Credits.HasCredits);
        Assert.Equal("12.50", codex.IndividualLimit!.Limit);
        Assert.Null(limits.Single(l => l.ResponseKey == "legacy").Credits!.Balance);
        Assert.Equal(130, limits.Single(l => l.ResponseKey == "named:reviews").Primary!.UsedPercent);
        Assert.DoesNotContain("secret-account", JsonSerializer.Serialize(evidence));
        Assert.DoesNotContain("do-not-retain", JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void MissingStateIsNotFalseAndMalformedMetadataDoesNotHideCurrentQuota()
    {
        var missing = CodexAppServerQuotaProvider.ParseQuotaResponse("""{"result":{"rateLimits":{}}}""", DateTimeOffset.UtcNow);
        Assert.Null(Assert.Single(missing.MetadataObservation!.QuotaMetadata!.Limits).SpendControlReached);
        Assert.Equal(AccountEvidenceClass.Unattributed, missing.MetadataObservation.AccountEvidence);
        var malformed = CodexAppServerQuotaProvider.ParseQuotaResponse("""
            {"result":{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300},"credits":{"balance":false}}}}
            """, DateTimeOffset.UtcNow);
        Assert.Equal(25, Assert.Single(malformed.Snapshots).UsedPercent);
        Assert.Equal(ServerEvidenceState.Invalid, malformed.MetadataObservation!.State);
        Assert.Null(malformed.MetadataObservation.QuotaMetadata);
    }
}
