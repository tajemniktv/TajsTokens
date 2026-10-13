// Taj's Tokens | LocalLimitsProjection.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using System.Security.Cryptography;
using System.Text;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Core.Services;

/// <summary>
///     Projects the existing current quota lanes. It never refreshes a provider, sums accounts,
///     turns token totals into subscription cost, or exposes account keys and display labels.
/// </summary>
public static class LocalLimitsProjection
{
    public const string Schema = "tajstokens.limits.v1";

    public static LocalLimitsEnvelope Build(TelemetrySnapshot snapshot, string idSalt, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(idSalt);
        IEnumerable<(QuotaSnapshot? Value, string Provider, string Profile, string Window, TelemetryHealthState State)> lanes =
            snapshot.QuotaLanes.Count > 0
                ? snapshot.QuotaLanes.Select(lane => (lane.Snapshot, lane.Provider, lane.Profile, lane.Kind.ToString(), lane.State))
                : snapshot.QuotaSnapshots.Select(value =>
                    (Value: (QuotaSnapshot?)value, value.Provider, value.Profile, Window: value.Kind.ToString(),
                        State: snapshot.IsQuotaSnapshotFresh(value) ? TelemetryHealthState.Live : TelemetryHealthState.Stale));

        LocalLimitResource[] resources = lanes.Select(lane =>
        {
            QuotaSnapshot? value = lane.Value;
            double? used = value?.UsedPercent is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100
                ? percent : null;
            string identity = value?.AccountKey is null ? "unknown" : "source-key-unverified";
            string idSource = string.Join('\u001f', lane.Provider, lane.Profile, lane.Window,
                value?.LimitId ?? value?.Lane ?? "unknown", value?.AccountKey ?? "unknown");
            byte[] digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(idSalt), Encoding.UTF8.GetBytes(idSource));
            string id = Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
            return new LocalLimitResource(
                id, lane.Provider, lane.Window, identity,
                value?.Source ?? "not-reported", value?.Authority.ToString() ?? "Unknown",
                lane.State.ToString().ToLowerInvariant(), used, used is null ? null : 100 - used,
                value?.WindowMinutes, value?.ResetsAtUtc, value?.CapturedAtUtc,
                value?.CollectedAtUtc ?? value?.CapturedAtUtc);
        }).ToArray();

        return new LocalLimitsEnvelope(Schema, nowUtc,
            snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? null : snapshot.CapturedAtUtc,
            snapshot.CapturedAtUtc == DateTimeOffset.MinValue ? "not-collected" : "available",
            resources);
    }
}
