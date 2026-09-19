using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

public enum CodexDriftKind { ReportedConfiguration, Missingness, Capability, HistoricalRevision, BlockingState }

/// <summary>Rebuildable signal referencing immutable fetches; not proof of a provider policy change.</summary>
public sealed record CodexDriftSignal(string Id, string Policy, string BeforeObservationId,
    string AfterObservationId, DateTimeOffset FirstObservedAtUtc, CodexServerSurface Surface,
    string? AccountKey, CodexDriftKind Kind, string Field, string? Before, string? After)
{
    // Fetch time does not establish when a policy became effective.
    public DateTimeOffset? EffectiveAtUtc => null;
}

public static class CodexEvidenceDrift
{
    public const string Policy = "codex-evidence-drift/v1";

    public static IReadOnlyList<CodexDriftSignal> Analyze(IEnumerable<CodexServerObservation> observations)
    {
        var signals = new List<CodexDriftSignal>();
        foreach (var surface in observations.Where(x => x.Surface is CodexServerSurface.QuotaMetadata or
                     CodexServerSurface.DailyCounts or CodexServerSurface.DailyRelativeUsage).GroupBy(x => x.Surface))
        {
            CodexServerObservation? prior = null;
            foreach (var current in surface.DistinctBy(x => x.Id).OrderBy(x => x.CollectedAtUtc).ThenBy(x => x.Id, StringComparer.Ordinal))
            {
                var before = prior;
                prior = current;
                // Never bridge A/B/A accounts, failed attempts, unknown identity or overlapping fetches.
                if (before is null || !Scoped(before) || !Scoped(current) ||
                    before.CorrelatedAccountKey != current.CorrelatedAccountKey ||
                    before.AccountEvidence != current.AccountEvidence || before.CollectedAtUtc >= current.FetchStartedAtUtc)
                    continue;

                void Change(string field, string? oldValue, string? newValue, CodexDriftKind kind)
                {
                    if (oldValue == newValue) return;
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Policy}|{before.Id}|{current.Id}|{field}")));
                    signals.Add(new(id, Policy, before.Id, current.Id, current.CollectedAtUtc, current.Surface,
                        current.CorrelatedAccountKey, oldValue is null || newValue is null ? CodexDriftKind.Missingness : kind,
                        field, oldValue, newValue));
                }

                if (before.ContractVersion != current.ContractVersion || before.ClientVersion != current.ClientVersion)
                {
                    Change("contract", before.ContractVersion, current.ContractVersion, CodexDriftKind.Capability);
                    Change("client-version", before.ClientVersion, current.ClientVersion, CodexDriftKind.Capability);
                    continue;
                }
                Change("response-state", before.State.ToString(), current.State.ToString(), CodexDriftKind.Capability);
                if (!Usable(before) || !Usable(current)) continue;
                if (before.QuotaMetadata is { } oldQuota && current.QuotaMetadata is { } newQuota)
                {
                    if (oldQuota.Contract != newQuota.Contract)
                    { Change("quota-contract", oldQuota.Contract, newQuota.Contract, CodexDriftKind.Capability); continue; }
                    // ResponseKey preserves named versus legacy alternatives. Do not pool them.
                    if (oldQuota.Limits.GroupBy(x => x.ResponseKey).Any(x => x.Count() > 1) ||
                        newQuota.Limits.GroupBy(x => x.ResponseKey).Any(x => x.Count() > 1)) continue;
                    var oldLimits = oldQuota.Limits.ToDictionary(x => x.ResponseKey);
                    var newLimits = newQuota.Limits.ToDictionary(x => x.ResponseKey);
                    foreach (var key in oldLimits.Keys.Union(newLimits.Keys).Order(StringComparer.Ordinal))
                    {
                        if (!oldLimits.TryGetValue(key, out var oldLimit) || !newLimits.TryGetValue(key, out var newLimit))
                        { Change(key + "/reported", oldLimits.ContainsKey(key) ? "present" : null, newLimits.ContainsKey(key) ? "present" : null, CodexDriftKind.Missingness); continue; }
                        var oldFields = Fields(oldLimit);
                        var newFields = Fields(newLimit);
                        foreach (var field in oldFields.Keys)
                            Change(key + "/" + field, oldFields[field], newFields[field],
                                field is "reached-type" or "spend-control-reached" ? CodexDriftKind.BlockingState : CodexDriftKind.ReportedConfiguration);
                    }
                }
                if (before.DailyReport is { } oldReport && current.DailyReport is { } newReport)
                {
                    if (oldReport.SourceContract != newReport.SourceContract || oldReport.Endpoint != newReport.Endpoint)
                    { Change("report-contract", oldReport.SourceContract + "/" + oldReport.Endpoint, newReport.SourceContract + "/" + newReport.Endpoint, CodexDriftKind.Capability); continue; }
                    Change("units", oldReport.Units, newReport.Units, CodexDriftKind.ReportedConfiguration);
                    Change("group-by", oldReport.GroupBy, newReport.GroupBy, CodexDriftKind.ReportedConfiguration);
                    Change("plan", oldReport.Plan, newReport.Plan, CodexDriftKind.ReportedConfiguration);
                    // Compare only overlapping completed UTC days with unchanged quantity/group semantics.
                    // Moving report ranges and an in-progress current day are not historical drift.
                    if (string.IsNullOrWhiteSpace(oldReport.Units) || oldReport.Units != newReport.Units || oldReport.GroupBy != newReport.GroupBy ||
                        oldReport.Days.GroupBy(x => x.Date).Any(x => x.Count() > 1) || newReport.Days.GroupBy(x => x.Date).Any(x => x.Count() > 1)) continue;
                    var oldDays = oldReport.Days.ToDictionary(x => x.Date);
                    var boundary = DateOnly.FromDateTime(before.FetchStartedAtUtc.UtcDateTime);
                    foreach (var day in newReport.Days)
                    {
                        if (!DateOnly.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                            date >= boundary || !oldDays.TryGetValue(day.Date, out var oldDay)) continue;
                        var oldFields = Fields(oldDay);
                        var newFields = Fields(day);
                        foreach (var field in oldFields.Keys.Union(newFields.Keys).Order(StringComparer.Ordinal))
                            Change(day.Date + "/" + field, oldFields.GetValueOrDefault(field), newFields.GetValueOrDefault(field), CodexDriftKind.HistoricalRevision);
                    }
                }
            }
        }
        return signals.OrderBy(x => x.FirstObservedAtUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool Scoped(CodexServerObservation row) => !string.IsNullOrEmpty(row.CorrelatedAccountKey) &&
        row.AccountEvidence is AccountEvidenceClass.ProviderVerified or AccountEvidenceClass.ServerCorrelated;
    private static bool Usable(CodexServerObservation row) => row.State is ServerEvidenceState.Available or ServerEvidenceState.Empty;
    private static string? Value<T>(T? value) where T : struct => value is null ? null :
        value.Value is decimal amount ? amount.ToString("G29", CultureInfo.InvariantCulture) : Convert.ToString(value.Value, CultureInfo.InvariantCulture);
    private static Dictionary<string, string?> Fields(CodexQuotaLimitMetadata row) => new()
    {
        ["limit-id"] = row.LimitId, ["plan"] = row.PlanType, ["normal-model"] = row.NormalModelSlug,
        ["primary-duration"] = Value(row.Primary?.WindowDurationMins), ["secondary-duration"] = Value(row.Secondary?.WindowDurationMins),
        ["reached-type"] = row.RateLimitReachedType, ["spend-control-reached"] = Value(row.SpendControlReached),
        ["individual-limit"] = DecimalText(row.IndividualLimit?.Limit), ["credits-unlimited"] = Value(row.Credits?.Unlimited)
        // Usage, balances, remaining percentages and reset timestamps are not policy-drift fields.
    };
    private static string? DecimalText(string? text) => decimal.TryParse(text, NumberStyles.Number,
        CultureInfo.InvariantCulture, out var value) ? value.ToString("G29", CultureInfo.InvariantCulture) : text;
    private static Dictionary<string, string?> Fields(CodexDailyReportRow row)
    {
        var fields = new Dictionary<string, string?>
        {
            ["credits"] = Value(row.Credits), ["on-demand-credits"] = Value(row.OnDemandCredits),
            ["uncached-input"] = Value(row.UncachedInputTokens), ["cached-input"] = Value(row.CachedInputTokens),
            ["output"] = Value(row.OutputTokens), ["total-tokens"] = Value(row.TotalTokens)
        };
        foreach (var field in row.SurfaceUsage ?? new Dictionary<string, decimal>()) fields["surface:" + field.Key] = Value<decimal>(field.Value);
        return fields;
    }
}
