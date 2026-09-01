using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Ingestion;

internal sealed partial class CodexRolloutParser
{
    public ParsedRolloutRecord Parse(RawSessionRecord record, RolloutParseState state)
    {
        var recordBytes = Math.Max(0, record.EndByteOffset - record.StartByteOffset);
        var sourceRecordId = BuildSourceRecordId(state.SourceIdentity, record.StartByteOffset, record.EndByteOffset);
        using var document = JsonDocument.Parse(record.Payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, "unknown", recordBytes, DateTimeOffset.UtcNow, state.OwnSessionId);
        }

        var topType = ReadString(root, "type") ?? "unknown";
        var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
            ? payloadElement
            : root;
        var nestedType = ReadString(payload, "type") ?? ReadString(payload, "event_type");
        var eventClass = ClassifyEvent(topType, nestedType);
        var timestamp = ReadTimestamp(root, "timestamp") ?? ReadTimestamp(payload, "timestamp") ?? DateTimeOffset.UtcNow;

        if (string.Equals(topType, "session_meta", StringComparison.OrdinalIgnoreCase))
        {
            var sessionId = ReadString(payload, "id") ?? ReadString(payload, "session_id");
            if (!string.IsNullOrWhiteSpace(sessionId) && state.IsOwnSessionMeta(sessionId))
            {
                state.EstablishOwnership(sessionId, payload, timestamp);
                var session = state.BuildSession(timestamp, "active");
                var agent = state.BuildAgent(timestamp, AgentRuntimeState.Running);
                var relationship = string.IsNullOrWhiteSpace(state.ParentSessionId)
                    ? null
                    : new AgentRelationship(state.ParentSessionId!, sessionId, timestamp);
                return new ParsedRolloutRecord(
                    sourceRecordId, eventClass, recordBytes, timestamp, sessionId,
                    session, agent, relationship, null, null, [], null);
            }

            // A filename without an owning UUID is intentionally storage-only. Choosing the first
            // session_meta is unsafe because child rollouts can start with copied parent history.
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId);
        }

        if (!state.OwnershipEstablished)
        {
            return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, null);
        }

        if (string.Equals(topType, "turn_context", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(nestedType, "turn_context", StringComparison.OrdinalIgnoreCase))
        {
            state.CurrentModel = ReadString(payload, "model") ?? state.CurrentModel;
            state.ReasoningEffort = ReadString(payload, "reasoning_effort") ?? ReadNestedString(payload, "reasoning", "effort") ?? state.ReasoningEffort;
            state.ContextWindowTokens = ReadLong(payload, "model_context_window") ?? state.ContextWindowTokens;
            var usageEvent = new UsageEvent(sourceRecordId, state.OwnSessionId!, timestamp, "turn_context", BuildTurnContextSummary(state), null);
            return new ParsedRolloutRecord(
                sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId,
                state.BuildSession(timestamp, "active"), state.BuildAgent(timestamp, AgentRuntimeState.Running), null,
                usageEvent, null, [], null);
        }

        if (IsTokenCount(topType, nestedType, payload))
        {
            var info = payload.TryGetProperty("info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object
                ? infoElement
                : payload;
            var totalUsage = info.TryGetProperty("total_token_usage", out var totalElement) && totalElement.ValueKind == JsonValueKind.Object
                ? totalElement
                : default;
            CodexCumulativeTokenObservation? tokenObservation = null;
            if (totalUsage.ValueKind == JsonValueKind.Object)
            {
                var lastUsage = info.TryGetProperty("last_token_usage", out var lastElement) && lastElement.ValueKind == JsonValueKind.Object
                    ? ParseTokenUsageSnapshot(lastElement)
                    : null;
                tokenObservation = new CodexCumulativeTokenObservation(
                    sourceRecordId,
                    state.FilePath,
                    state.OwnSessionId!,
                    state.OwnSessionId,
                    timestamp,
                    state.CurrentModel,
                    state.ReasoningEffort,
                    ReadLong(totalUsage, "input_tokens") ?? 0,
                    ReadLong(totalUsage, "cached_input_tokens") ?? 0,
                    ReadLong(totalUsage, "cache_write_input_tokens") ?? 0,
                    ReadLong(totalUsage, "output_tokens") ?? 0,
                    ReadLong(totalUsage, "reasoning_output_tokens") ?? 0,
                    ReadLong(totalUsage, "total_tokens") ?? 0,
                    lastUsage);
            }

            var contextWindow = ReadLong(info, "model_context_window") ?? state.ContextWindowTokens;
            if (contextWindow is > 0)
            {
                state.ContextWindowTokens = contextWindow;
            }

            var lastInput = tokenObservation?.LastTokenUsage?.InputTokens;

            var context = lastInput is null && contextWindow is null
                ? null
                : new CodexContextObservation(
                    sourceRecordId + ":ctx", state.OwnSessionId!, state.OwnSessionId, timestamp,
                    state.CurrentModel, lastInput, contextWindow, false, recordBytes);

            var quotaSnapshots = ParseRateLimits(payload, timestamp);
            var usageEvent = new UsageEvent(sourceRecordId + ":token", state.OwnSessionId!, timestamp, "token_count", "Codex token/quota observation", null);
            return new ParsedRolloutRecord(
                sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId,
                state.BuildSession(timestamp, "active"), state.BuildAgent(timestamp, AgentRuntimeState.Running), null,
                usageEvent, tokenObservation, quotaSnapshots, context);
        }

        var isCompaction = string.Equals(topType, "compacted", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(nestedType, "context_compaction", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(nestedType, "compacted", StringComparison.OrdinalIgnoreCase);
        if (isCompaction)
        {
            var context = new CodexContextObservation(
                sourceRecordId + ":ctx", state.OwnSessionId!, state.OwnSessionId, timestamp,
                state.CurrentModel, null, state.ContextWindowTokens, true, recordBytes);
            var usageEvent = new UsageEvent(sourceRecordId, state.OwnSessionId!, timestamp, "compaction", "Context compaction", null);
            return new ParsedRolloutRecord(
                sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId,
                state.BuildSession(timestamp, "active"), state.BuildAgent(timestamp, AgentRuntimeState.Running), null,
                usageEvent, null, [], context);
        }

        var lifecycleState = MapLifecycleState(nestedType ?? topType);
        var shouldRecordTimeline = ShouldRecordTimeline(topType, nestedType);
        if (lifecycleState is not null || shouldRecordTimeline)
        {
            var stateValue = lifecycleState ?? AgentRuntimeState.Running;
            var status = stateValue switch
            {
                AgentRuntimeState.Completed => "completed",
                AgentRuntimeState.Failed => "failed",
                AgentRuntimeState.Waiting => "waiting",
                _ => "active"
            };
            var normalizedType = NormalizeTimelineType(topType, nestedType);
            var usageEvent = new UsageEvent(sourceRecordId, state.OwnSessionId!, timestamp, normalizedType, BuildSafeSummary(normalizedType), null);
            return new ParsedRolloutRecord(
                sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId,
                state.BuildSession(timestamp, status), state.BuildAgent(timestamp, stateValue), null,
                usageEvent, null, [], null);
        }

        return ParsedRolloutRecord.StorageOnly(sourceRecordId, eventClass, recordBytes, timestamp, state.OwnSessionId);
    }

    private static IReadOnlyList<QuotaSnapshot> ParseRateLimits(JsonElement payload, DateTimeOffset timestamp)
    {
        if (!payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var results = new List<QuotaSnapshot>();
        foreach (var property in limits.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var window = ReadInt(property.Value, "window_minutes");
            var used = ReadDouble(property.Value, "used_percent");
            if (window is null || used is null)
            {
                continue;
            }

            QuotaWindowKind? kind = window.Value switch
            {
                300 => QuotaWindowKind.FiveHour,
                10_080 => QuotaWindowKind.Weekly,
                _ => null
            };

            // The shared quota table currently has one identity per known kind/timestamp. Persisting
            // multiple arbitrary lanes as Unknown would silently collide, so keep them out until the
            // canonical quota model gains lane-aware identity.
            if (kind is null)
            {
                continue;
            }

            results.Add(new QuotaSnapshot(
                kind.Value,
                timestamp,
                used,
                window,
                ReadTimestamp(property.Value, "resets_at"),
                "codex",
                "default",
                $"codex-rollout:{property.Name}"));
        }

        return results;
    }

    private static CodexTokenUsageSnapshot ParseTokenUsageSnapshot(JsonElement usage) =>
        new(
            ReadLong(usage, "input_tokens"),
            ReadLong(usage, "cached_input_tokens"),
            ReadLong(usage, "cache_write_input_tokens"),
            ReadLong(usage, "output_tokens"),
            ReadLong(usage, "reasoning_output_tokens"),
            ReadLong(usage, "total_tokens"));

    private static bool IsTokenCount(string topType, string? nestedType, JsonElement payload) =>
        string.Equals(nestedType, "token_count", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(topType, "token_count", StringComparison.OrdinalIgnoreCase) ||
        (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("total_token_usage", out _));

    private static bool ShouldRecordTimeline(string topType, string? nestedType)
    {
        var type = nestedType ?? topType;
        return type is "task_started" or "task_complete" or "turn_aborted" or "function_call" or
            "function_call_output" or "custom_tool_call" or "custom_tool_call_output" or
            "agent_message" or "thread_goal_updated" or "thread_settings_applied" or
            "spawn_agent" or "wait_agent" or "send_message" or "interrupt_agent" or "followup_task";
    }

    private static AgentRuntimeState? MapLifecycleState(string type) => type switch
    {
        "task_started" => AgentRuntimeState.Running,
        "task_complete" => AgentRuntimeState.Completed,
        "turn_aborted" => AgentRuntimeState.Waiting,
        "wait_agent" => AgentRuntimeState.Waiting,
        _ => null
    };

    private static string NormalizeTimelineType(string topType, string? nestedType)
    {
        var type = nestedType ?? topType;
        return type.Length <= 64 ? type : type[..64];
    }

    private static string BuildSafeSummary(string normalizedType) => normalizedType switch
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
        _ => normalizedType.Replace('_', ' ')
    };

    private static string BuildTurnContextSummary(RolloutParseState state)
    {
        var model = string.IsNullOrWhiteSpace(state.CurrentModel) ? "unknown model" : state.CurrentModel;
        var reasoning = string.IsNullOrWhiteSpace(state.ReasoningEffort) ? "unknown reasoning" : state.ReasoningEffort;
        return $"Turn context: {model}, {reasoning}";
    }

    private static string ClassifyEvent(string topType, string? nestedType)
    {
        var type = nestedType ?? topType;
        return type.Length <= 80 ? type : type[..80];
    }

    private static string BuildSourceRecordId(string sourceIdentity, long start, long end)
    {
        var bytes = Encoding.UTF8.GetBytes($"{sourceIdentity}:{start}:{end}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string? ExtractSessionIdFromFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var matches = SessionIdRegex().Matches(name);
        return matches.Count == 0 ? null : matches[^1].Value;
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return ReadString(nested, propertyName);
        }
        return null;
    }

    internal static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    internal static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }
        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        var value = ReadLong(element, propertyName);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }
        return property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    internal static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numeric))
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

    public CodexSession BuildSession(DateTimeOffset activityAtUtc, string status) =>
        new(OwnSessionId!, OwnSessionId, Repository, StartedAtUtc == default ? activityAtUtc : StartedAtUtc, activityAtUtc, status);

    public Agent BuildAgent(DateTimeOffset activityAtUtc, AgentRuntimeState state) =>
        new(OwnSessionId!, OwnSessionId!, AgentName, state, activityAtUtc, CurrentModel);

    private static string? FindString(JsonElement element, string propertyName, int depth = 0)
    {
        var direct = CodexRolloutParser.ReadString(element, propertyName);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (depth >= 4)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nested = FindString(property.Value, propertyName, depth + 1);
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
    CodexCumulativeTokenObservation? TokenObservation,
    IReadOnlyList<QuotaSnapshot> QuotaSnapshots,
    CodexContextObservation? ContextObservation)
{
    public bool HasNormalizedTelemetry =>
        Session is not null || Agent is not null || Relationship is not null || UsageEvent is not null ||
        TokenObservation is not null || QuotaSnapshots.Count > 0 || ContextObservation is not null;

    public static ParsedRolloutRecord StorageOnly(
        string sourceRecordId,
        string eventClass,
        long recordBytes,
        DateTimeOffset timestamp,
        string? sessionId) =>
        new(sourceRecordId, eventClass, recordBytes, timestamp, sessionId, null, null, null, null, null, [], null);
}
