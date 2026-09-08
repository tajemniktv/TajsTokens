namespace TajsTokens.Core.Models;

/// <summary>Response scope survives even when Codex reports no supported quota windows.</summary>
public sealed record CodexQuotaResponse(IReadOnlyList<QuotaSnapshot> Snapshots, string? AccountKey);
