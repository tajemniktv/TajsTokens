using System.Globalization;
using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Services;

/// <summary>Only allowlisted model metadata leaves this reader. Identity, instructions and source JSON are never retained.</summary>
internal static class CodexModelCacheReader
{
    public static async Task<CodexModelCatalogRead> ReadAsync(string codexHome, CancellationToken token)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        CodexModelCatalogRead Failure(CodexModelCatalogReadState state, string issue) => new(state, started, DateTimeOffset.UtcNow, null, null, issue);
        try
        {
            string path = Path.Combine(codexHome, "models_cache.json");
            if (!File.Exists(path)) return Failure(CodexModelCatalogReadState.Absent, "cache-absent");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            if (stream.Length is <= 0 or > 4 * 1024 * 1024) return Failure(CodexModelCatalogReadState.Invalid, "cache-size-bound");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            if (stream.ReadByte() != -1) return Failure(CodexModelCatalogReadState.Invalid, "cache-changed-during-read");
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            var fetched = DateTimeOffset.Parse(root.GetProperty("fetched_at").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            var array = root.GetProperty("models");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > CodexModelCatalogDefinition.MaximumModels)
                return Failure(CodexModelCatalogReadState.Invalid, "model-row-bound");
            var rows = array.EnumerateArray().Select(m =>
            {
                var upgrade = m.TryGetProperty("upgrade", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                DateTimeOffset? retirement = Text(upgrade, "retirement_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
                string[] capabilities = new[] { "supports_search_tool", "supports_image_detail_original", "supports_reasoning_effort_updates", "supports_experimental_context" }
                    .Where(k => Boolean(m, k) == true).ToArray();
                return new CodexModelMetadata(m.GetProperty("slug").GetString()!, Text(m, "display_name"), [],
                    Items(m, "supported_reasoning_levels", "effort"), Text(m, "default_reasoning_level"), Items(m, "input_modalities"),
                    Items(m, "service_tiers", "id"), Number(m, "context_window"), Number(m, "max_context_window"), Boolean(m, "supported_in_api"),
                    Text(m, "visibility"), retirement, Text(upgrade, "id"), capabilities);
            }).ToArray();
            var definition = new CodexModelCatalogDefinition(CodexModelCatalogDefinition.Version, "codex-home/models_cache.json", Text(root, "client_version"), rows);
            return new(CodexModelCatalogReadState.Available, started, DateTimeOffset.UtcNow, fetched, definition, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Failure(CodexModelCatalogReadState.Unavailable, "cache-read-unavailable"); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Failure(CodexModelCatalogReadState.Invalid, "unsupported-cache-metadata"); }
    }
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    private static long? Number(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt64() : null;
    private static bool? Boolean(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : null;
    private static string[] Items(JsonElement e, string key, string? child = null)
    {
        if (!e.TryGetProperty(key, out var array) || array.ValueKind == JsonValueKind.Null) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 32) throw new JsonException();
        return array.EnumerateArray().Select(x => child is null ? x.GetString()! : x.GetProperty(child).GetString()!).ToArray();
    }
}
