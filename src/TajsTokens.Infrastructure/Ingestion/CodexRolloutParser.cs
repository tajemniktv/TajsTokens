// Taj's Tokens | CodexRolloutParser.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

#endregion

namespace TajsTokens.Infrastructure.Ingestion;

internal sealed partial class CodexRolloutParser
{
    public ParsedRolloutRecord Parse(RawSessionRecord record, RolloutParseState state)
    {
        long recordBytes = Math.Max(0, record.EndByteOffset - record.StartByteOffset);
        string sourceRecordId = BuildSourceRecordId(state.SourceIdentity, record.StartByteOffset, record.EndByteOffset);
        using JsonDocument document = JsonDocument.Parse(record.Payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, "unknown", recordBytes, DateTimeOffset.UtcNow, state.OwnSessionId);
        }

        string topType = ReadString(root, "type") ?? "unknown";
        JsonElement payload =
            root.TryGetProperty("payload", out JsonElement payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                ? payloadElement
                : root;
        string? nestedType = ReadString(payload, "type") ?? ReadString(payload, "event_type");
        string eventClass = ClassifyEvent(topType, nestedType);
        DateTimeOffset? sourceTimestamp = ReadTimestamp(root, "timestamp") ?? ReadTimestamp(payload, "timestamp");
        DateTimeOffset timestamp = sourceTimestamp ?? DateTimeOffset.UtcNow;

        CodexWorkloadObservation Workload(string type)
        {
            return new CodexWorkloadObservation(
                sourceRecordId,
                state.SourceIdentity,
                state.FilePath,
                record.StartByteOffset,
                record.EndByteOffset,
                state.OwnSessionId!,
                type,
                sourceTimestamp,
                DateTimeOffset.UtcNow,
                ReadString(payload, "turn_id"),
                ReadString(payload, "root_turn_id"),
                type == "session_meta" ? state.ParentSessionId : null,
                type == "turn_context" ? ReadString(payload, "model") : null,
                type == "turn_context" ? ReadEffort(payload) : null,
                ReadLong(payload, "model_context_window"),
                StartedAtUnixSeconds: ReadLong(payload, "started_at"),
                CompletedAtUnixSeconds: ReadLong(payload, "completed_at"),
                DurationMilliseconds: ReadLong(payload, "duration_ms"),
                TimeToFirstTokenMilliseconds: ReadLong(payload, "time_to_first_token_ms"),
                SessionSourceKind: type == "session_meta" ? ReadSessionSourceKind(payload) : null,
                ServiceTier: type == "thread_settings_applied" ? ReadServiceTier(payload) : null);
        }

        if (string.Equals(topType, "session_meta", StringComparison.OrdinalIgnoreCase))
        {
            string? sessionId = ReadString(payload, "id") ?? ReadString(payload, "session_id");
            if (!string.IsNullOrWhiteSpace(sessionId) && state.IsOwnSessionMeta(sessionId))
            {
                state.EstablishOwnership(sessionId, payload, timestamp);
                CodexSession session = state.BuildSession(timestamp, "active");
                Agent agent = state.BuildAgent(timestamp, AgentRuntimeState.Running);
                AgentRelationship? relationship = string.IsNullOrWhiteSpace(state.ParentSessionId)
                    ? null
                    : new AgentRelationship(state.ParentSessionId!, sessionId, timestamp);
                return new ParsedRolloutRecord(
                    sourceRecordId,
                    eventClass,
                    recordBytes,
                    timestamp,
                    sessionId,
                    session,
                    agent,
                    relationship,
                    null,
                    null,
                    [],
                    null,
                    Workload("session_meta"));
            }

            // A filename without an owning UUID is intentionally storage-only. Choosing the first
            // session_meta is unsafe because child rollouts can start with copied parent history.
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId);
        }

        if (!state.OwnershipEstablished)
        {
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, null);
        }

        if (topType == "token_usage_record")
        {
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId)
                with
                {
                    ResponseObservation = CodexResponseObservationParser.Parse(
                        payload,
                        record,
                        sourceRecordId,
                        state.SourceIdentity,
                        state.OwnSessionId!,
                        sourceTimestamp),
                };
        }

        if (topType == "event_msg" && nestedType == "thread_settings_applied")
        {
            // A source setting, not an executed/billed tier. No carry-forward to token events:
            // first-turn absence and interleaved streams cannot establish that attribution.
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId)
                with
                {
                    WorkloadObservation = Workload("thread_settings_applied"),
                };
        }

        if (string.Equals(topType, "turn_context", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nestedType, "turn_context", StringComparison.OrdinalIgnoreCase))
        {
            state.CurrentModel = ReadString(payload, "model") ?? state.CurrentModel;
            // Turn context is the new baseline. Absent/null effort must not borrow the prior
            // turn's value, especially across a model change. Current native format uses effort.
            state.ReasoningEffort = ReadEffort(payload);
            state.ContextWindowTokens = ReadLong(payload, "model_context_window") ?? state.ContextWindowTokens;
            var usageEvent = new UsageEvent(
                sourceRecordId,
                state.OwnSessionId!,
                timestamp,
                "turn_context",
                BuildTurnContextSummary(state),
                null);
            return new ParsedRolloutRecord(
                sourceRecordId,
                eventClass,
                recordBytes,
                timestamp,
                state.OwnSessionId,
                state.BuildSession(timestamp, "active"),
                state.BuildAgent(timestamp, AgentRuntimeState.Running),
                null,
                usageEvent,
                null,
                [],
                null,
                Workload("turn_context"));
        }

        if (IsTokenCount(topType, nestedType, payload))
        {
            JsonElement info = payload.TryGetProperty("info", out JsonElement infoElement) && infoElement.ValueKind == JsonValueKind.Object
                ? infoElement
                : payload;
            JsonElement totalUsage = info.TryGetProperty("total_token_usage", out JsonElement totalElement) &&
                                     totalElement.ValueKind == JsonValueKind.Object
                ? totalElement
                : default;
            CodexTokenUsageSnapshot? totalSnapshot = totalUsage.ValueKind == JsonValueKind.Object
                ? ParseTokenUsageSnapshot(totalUsage)
                : null;
            CodexTokenUsageSnapshot? lastUsage =
                info.TryGetProperty("last_token_usage", out JsonElement lastElement) && lastElement.ValueKind == JsonValueKind.Object
                    ? ParseTokenUsageSnapshot(lastElement)
                    : null;
            CodexTokenCountObservation? tokenObservation = null;
            if (totalSnapshot is not null || lastUsage is not null)
            {
                tokenObservation = new CodexTokenCountObservation(
                    sourceRecordId,
                    state.FilePath,
                    state.OwnSessionId!,
                    state.OwnSessionId,
                    timestamp,
                    state.CurrentModel,
                    state.ReasoningEffort,
                    totalSnapshot,
                    lastUsage);
            }

            long? contextWindow = ReadLong(info, "model_context_window") ?? state.ContextWindowTokens;
            if (contextWindow is > 0)
            {
                state.ContextWindowTokens = contextWindow;
            }

            long? lastInput = lastUsage?.InputTokens;

            CodexContextObservation? context = lastInput is null && contextWindow is null
                ? null
                : new CodexContextObservation(
                    sourceRecordId + ":ctx",
                    state.OwnSessionId!,
                    state.OwnSessionId,
                    timestamp,
                    state.CurrentModel,
                    lastInput,
                    contextWindow,
                    false,
                    recordBytes);

            QuotaSnapshot[] quotaSnapshots = ParseRateLimits(payload, timestamp).Select(snapshot => snapshot with
            {
                ObservationId = sourceRecordId + ":" + snapshot.Lane,
                SourceIdentity = state.SourceIdentity,
                SessionId = state.OwnSessionId,
                CollectedAtUtc = DateTimeOffset.UtcNow,
                HasSourceTimestamp = sourceTimestamp is not null,
            }).ToArray();
            var usageEvent = new UsageEvent(
                sourceRecordId + ":token",
                state.OwnSessionId!,
                timestamp,
                "token_count",
                "Codex token/quota observation",
                null);
            return new ParsedRolloutRecord(
                sourceRecordId,
                eventClass,
                recordBytes,
                timestamp,
                state.OwnSessionId,
                state.BuildSession(timestamp, "active"),
                state.BuildAgent(timestamp, AgentRuntimeState.Running),
                null,
                usageEvent,
                tokenObservation,
                quotaSnapshots,
                context);
        }

        bool isCompaction = string.Equals(topType, "compacted", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(nestedType, "context_compaction", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(nestedType, "compacted", StringComparison.OrdinalIgnoreCase);
        if (isCompaction)
        {
            var context = new CodexContextObservation(
                sourceRecordId + ":ctx",
                state.OwnSessionId!,
                state.OwnSessionId,
                timestamp,
                state.CurrentModel,
                null,
                state.ContextWindowTokens,
                true,
                recordBytes);
            var usageEvent = new UsageEvent(sourceRecordId, state.OwnSessionId!, timestamp, "compaction", "Context compaction", null);
            return new ParsedRolloutRecord(
                sourceRecordId,
                eventClass,
                recordBytes,
                timestamp,
                state.OwnSessionId,
                state.BuildSession(timestamp, "active"),
                state.BuildAgent(timestamp, AgentRuntimeState.Running),
                null,
                usageEvent,
                null,
                [],
                context);
        }

        AgentRuntimeState? lifecycleState = MapLifecycleState(nestedType ?? topType);
        bool shouldRecordTimeline = ShouldRecordTimeline(topType, nestedType);
        if (lifecycleState is not null || shouldRecordTimeline)
        {
            AgentRuntimeState stateValue = lifecycleState ?? AgentRuntimeState.Running;
            string status = stateValue switch
            {
                AgentRuntimeState.Completed => "completed",
                AgentRuntimeState.Failed => "failed",
                AgentRuntimeState.Waiting => "waiting",
                _ => "active",
            };
            string normalizedType = NormalizeTimelineType(topType, nestedType);
            var usageEvent = new UsageEvent(
                sourceRecordId,
                state.OwnSessionId!,
                timestamp,
                normalizedType,
                BuildSafeSummary(normalizedType),
                null);
            return new ParsedRolloutRecord(
                sourceRecordId,
                eventClass,
                recordBytes,
                timestamp,
                state.OwnSessionId,
                state.BuildSession(timestamp, status),
                state.BuildAgent(timestamp, stateValue),
                null,
                usageEvent,
                null,
                [],
                null,
                normalizedType is "task_started" or "task_complete" or "turn_aborted" ? Workload(normalizedType) : null);
        }

        return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId);
    }

    private static string? ReadServiceTier(JsonElement payload)
    {
        if (!payload.TryGetProperty("thread_settings", out JsonElement settings) || settings.ValueKind != JsonValueKind.Object ||
            settings.EnumerateObject().Count(p => p.NameEquals("service_tier")) != 1) return null;
        JsonElement value = settings.GetProperty("service_tier");
        string? tier = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return tier is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(tier) && !tier.Any(char.IsControl) ? tier : null;
    }

    private static string? ReadEffort(JsonElement payload)
    {
        return payload.TryGetProperty("effort", out _)
            ? ReadString(payload, "effort")
            : ReadString(payload, "reasoning_effort") ?? ReadNestedString(payload, "reasoning", "effort");
    }

    private static string? ReadSessionSourceKind(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out JsonElement source)) return null;
        if (source.ValueKind == JsonValueKind.String) return source.GetString();
        // Only the native enum discriminator, never custom source payload text.
        if (source.ValueKind == JsonValueKind.Object && source.EnumerateObject().Count() == 1)
            return source.EnumerateObject().First().Name;
        return null;
    }

    private static IReadOnlyList<QuotaSnapshot> ParseRateLimits(JsonElement payload, DateTimeOffset timestamp)
    {
        if (!payload.TryGetProperty("rate_limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var results = new List<QuotaSnapshot>();
        foreach (JsonProperty property in limits.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int? window = ReadInt(property.Value, "window_minutes");
            double? used = ReadDouble(property.Value, "used_percent");
            if (window is null || used is null)
            {
                continue;
            }

            QuotaWindowKind? kind = window.Value switch
            {
                300 => QuotaWindowKind.FiveHour,
                10_080 => QuotaWindowKind.Weekly,
                _ => null,
            };

            results.Add(
                new QuotaSnapshot(
                    kind ?? QuotaWindowKind.Unknown,
                    timestamp,
                    used,
                    window,
                    ReadTimestamp(property.Value, "resets_at"),
                    "codex",
                    "default",
                    $"codex-rollout:{property.Name}")
                {
                    LimitId = ReadString(limits, "limit_id"), PlanType = ReadString(limits, "plan_type"), Lane = property.Name,
                });
        }

        return results;
    }

    private static CodexTokenUsageSnapshot ParseTokenUsageSnapshot(JsonElement usage)
    {
        return new CodexTokenUsageSnapshot(
            ReadLong(usage, "input_tokens"),
            ReadLong(usage, "cached_input_tokens"),
            ReadLong(usage, "cache_write_input_tokens"),
            ReadLong(usage, "output_tokens"),
            ReadLong(usage, "reasoning_output_tokens"),
            ReadLong(usage, "total_tokens"));
    }

    private static bool IsTokenCount(string topType, string? nestedType, JsonElement payload)
    {
        return string.Equals(nestedType, "token_count", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(topType, "token_count", StringComparison.OrdinalIgnoreCase) ||
               (payload.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("total_token_usage", out _));
    }

    private static bool ShouldRecordTimeline(string topType, string? nestedType)
    {
        string type = nestedType ?? topType;
        return type is "task_started" or "task_complete" or "turn_aborted" or "function_call" or
            "function_call_output" or "custom_tool_call" or "custom_tool_call_output" or
            "agent_message" or "thread_goal_updated" or "thread_settings_applied" or
            "spawn_agent" or "wait_agent" or "send_message" or "interrupt_agent" or "followup_task";
    }

    private static AgentRuntimeState? MapLifecycleState(string type)
    {
        return type switch
        {
            "task_started" => AgentRuntimeState.Running,
            "task_complete" => AgentRuntimeState.Completed,
            "turn_aborted" => AgentRuntimeState.Waiting,
            "wait_agent" => AgentRuntimeState.Waiting,
            _ => null,
        };
    }

    private static string NormalizeTimelineType(string topType, string? nestedType)
    {
        string type = nestedType ?? topType;
        return type.Length <= 64 ? type : type[..64];
    }

    private static string BuildSafeSummary(string normalizedType)
    {
        return normalizedType switch
        {
            "task_started" => "Task started",
            "task_complete" => "Task completed",
            "turn_aborted" => "Turn interrupted",
            "function_call" or "custom_tool_call" => "Tool call",
            "function_call_output" or "custom_tool_call_output" => "Tool call completed",
            "spawn_agent" => "Subagent spawned",
            "wait_agent" => "Waiting for agent",
            "send_message" => "Agent communication",
            "interrupt_agent" => "Agent interrupted",
            "followup_task" => "Follow-up task",
            _ => normalizedType.Replace('_', ' '),
        };
    }

    private static string BuildTurnContextSummary(RolloutParseState state)
    {
        string model = string.IsNullOrWhiteSpace(state.CurrentModel) ? "unknown model" : state.CurrentModel;
        string reasoning = string.IsNullOrWhiteSpace(state.ReasoningEffort) ? "unknown reasoning" : state.ReasoningEffort;
        return $"Turn context: {model}, {reasoning}";
    }

    private static string ClassifyEvent(string topType, string? nestedType)
    {
        string type = nestedType ?? topType;
        return type.Length <= 80 ? type : type[..80];
    }

    private static string BuildSourceRecordId(string sourceIdentity, long start, long end)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{sourceIdentity}:{start}:{end}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string? ExtractSessionIdFromFileName(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        // Observed Desktop form: the trailing UUID is a file suffix, not the thread owner.
        // Anchor the entire known shape; do not choose the first arbitrary UUID in renamed copies.
        Match desktop = DesktopRolloutNameRegex().Match(name);
        if (desktop.Success) return desktop.Groups["owner"].Value;
        MatchCollection matches = SessionIdRegex().Matches(name);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    internal static bool HasDesktopFilenameSuffix(string filePath)
    {
        return DesktopRolloutNameRegex().IsMatch(Path.GetFileNameWithoutExtension(filePath));
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (element.TryGetProperty(objectName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return ReadString(nested, propertyName);
        }
        return null;
    }

    internal static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    internal static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long number))
        {
            return number;
        }
        return property.ValueKind == JsonValueKind.String && long.TryParse(
            property.GetString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        long? value = ReadLong(element, propertyName);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out double number))
        {
            return number;
        }
        return property.ValueKind == JsonValueKind.String && double.TryParse(
            property.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    internal static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                property.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            return timestamp.ToUniversalTime();
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long numeric))
        {
            try
            {
                return numeric > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return null;
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex SessionIdRegex();

    [GeneratedRegex(
        @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<owner>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex DesktopRolloutNameRegex();
}

internal sealed class RolloutParseState
{
    public RolloutParseState(string filePath, string sourceIdentity, CodexParserResumeState? resumeState = null)
    {
        FilePath = filePath;
        SourceIdentity = sourceIdentity;
        ExpectedSessionId = CodexRolloutParser.ExtractSessionIdFromFileName(filePath);

        if (resumeState is not null && string.Equals(resumeState.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
        {
            OwnSessionId = resumeState.SessionId;
            ParentSessionId = resumeState.ParentSessionId;
            AgentName = resumeState.AgentName;
            Repository = resumeState.Repository;
            StartedAtUtc = resumeState.StartedAtUtc;
            CurrentModel = resumeState.CurrentModel;
            ReasoningEffort = resumeState.ReasoningEffort;
            ContextWindowTokens = resumeState.ContextWindowTokens;
        }
    }

    public string FilePath { get; }
    public string SourceIdentity { get; }
    public string? ExpectedSessionId { get; }
    public string? OwnSessionId { get; private set; }
    public string? ParentSessionId { get; private set; }
    public string AgentName { get; private set; } = "Codex session";
    public string Repository { get; private set; } = "(unknown)";
    public DateTimeOffset StartedAtUtc { get; private set; }
    public string? CurrentModel { get; set; }
    public string? ReasoningEffort { get; set; }
    public long? ContextWindowTokens { get; set; }
    public bool OwnershipEstablished => OwnSessionId is not null;

    public bool IsOwnSessionMeta(string sessionId)
    {
        if (ExpectedSessionId is not null)
        {
            return string.Equals(sessionId, ExpectedSessionId, StringComparison.OrdinalIgnoreCase);
        }

        // A no-UUID source may only continue an already corroborated restored owner. Never claim the
        // first session_meta in a fresh scan because it may belong to an inherited parent prefix.
        return OwnSessionId is not null && string.Equals(OwnSessionId, sessionId, StringComparison.OrdinalIgnoreCase);
    }

    public void EstablishOwnership(string sessionId, JsonElement payload, DateTimeOffset timestamp)
    {
        OwnSessionId = sessionId;
        ParentSessionId = FindString(payload, "parent_thread_id") ?? FindString(payload, "parent_session_id") ?? ParentSessionId;
        AgentName = FindString(payload, "agent_nickname") ??
                    (string.IsNullOrWhiteSpace(ParentSessionId) ? "Root agent" : "Subagent");
        Repository = FindString(payload, "cwd") ?? FindString(payload, "repository") ?? Repository;
        StartedAtUtc = CodexRolloutParser.ReadTimestamp(payload, "timestamp") ?? (StartedAtUtc == default ? timestamp : StartedAtUtc);
        CurrentModel = FindString(payload, "model") ?? CurrentModel;
    }

    public CodexParserResumeState? BuildResumeState(long byteOffset)
    {
        if (OwnSessionId is null)
        {
            return null;
        }

        return new CodexParserResumeState(
            SourceIdentity,
            byteOffset,
            OwnSessionId,
            ParentSessionId,
            AgentName,
            Repository,
            StartedAtUtc,
            CurrentModel,
            ReasoningEffort,
            ContextWindowTokens,
            DateTimeOffset.UtcNow);
    }

    public CodexSession BuildSession(DateTimeOffset activityAtUtc, string status)
    {
        return new CodexSession(
            OwnSessionId!,
            OwnSessionId,
            Repository,
            StartedAtUtc == default ? activityAtUtc : StartedAtUtc,
            activityAtUtc,
            status);
    }

    public Agent BuildAgent(DateTimeOffset activityAtUtc, AgentRuntimeState state)
    {
        return new Agent(OwnSessionId!, OwnSessionId!, AgentName, state, activityAtUtc, CurrentModel);
    }

    private static string? FindString(JsonElement element, string propertyName, int depth = 0)
    {
        string? direct = CodexRolloutParser.ReadString(element, propertyName);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (depth >= 4)
        {
            return null;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? nested = FindString(property.Value, propertyName, depth + 1);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }
        return null;
    }
}

internal sealed record ParsedRolloutRecord(
    string SourceRecordId,
    string EventClass,
    long RecordBytes,
    DateTimeOffset TimestampUtc,
    string? SessionId,
    CodexSession? Session,
    Agent? Agent,
    AgentRelationship? Relationship,
    UsageEvent? UsageEvent,
    CodexTokenCountObservation? TokenObservation,
    IReadOnlyList<QuotaSnapshot> QuotaSnapshots,
    CodexContextObservation? ContextObservation,
    CodexWorkloadObservation? WorkloadObservation = null,
    CodexResponseObservation? ResponseObservation = null)
{
    public bool HasNormalizedTelemetry =>
        Session is not null || Agent is not null || Relationship is not null || UsageEvent is not null ||
        TokenObservation is not null || QuotaSnapshots.Count > 0 || ContextObservation is not null || WorkloadObservation is not null ||
        ResponseObservation is not null;

    public static ParsedRolloutRecord StorageOnly(
        string sourceRecordId,
        string eventClass,
        long recordBytes,
        DateTimeOffset timestamp,
        string? sessionId)
    {
        return new ParsedRolloutRecord(
            sourceRecordId,
            eventClass,
            recordBytes,
            timestamp,
            sessionId,
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }
}