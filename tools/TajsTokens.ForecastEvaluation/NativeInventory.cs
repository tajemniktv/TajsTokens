using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>Read-only content-free inventory. No raw JSON or identifiers are printed or saved.</summary>
internal static class NativeInventory
{
    public static void Run(string codexHome, string telemetryPath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = telemetryPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT DISTINCT session_id FROM codex_native_token_events";
        var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = query.ExecuteReader()) while (reader.Read()) imported.Add(reader.GetString(0));
        var files = new[] { "sessions", "archived_sessions" }.Select(x => Path.Combine(codexHome, x))
            .Where(Directory.Exists).SelectMany(x => Directory.EnumerateFiles(x, "*.jsonl", SearchOption.AllDirectories)).ToArray();
        var counts = new SortedDictionary<string, long>();
        var dates = new Dictionary<string, (DateTimeOffset Min, DateTimeOffset Max)>();
        var effortCounts = new SortedDictionary<string, long>();
        void Add(string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        foreach (var path in files)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase);
            if (!match.Success) { Add("files.no_owner_identity"); continue; }
            var own = match.Value;
            var established = false;
            string? effort = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                    string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    var type = Text(root, "type");
                    if (type == "session_meta")
                    {
                        if (Text(payload, "id") == own)
                        {
                            established = true;
                            if (payload.TryGetProperty("source", out var source))
                            {
                                var sourceKind = source.ValueKind == JsonValueKind.String ? source.GetString()
                                    : source.ValueKind == JsonValueKind.Object ? source.EnumerateObject().FirstOrDefault().Name : null;
                                Add("session_meta.source_kind." + (sourceKind is "cli" or "vscode" or "exec" or "mcp" or "subagent" or "internal" ? sourceKind : "other_or_missing"));
                            }
                        }
                        else Add("inherited.session_meta");
                        continue;
                    }
                    if (!established) continue;
                    var nested = Text(payload, "type");
                    if (type is not "turn_context" && nested is not ("task_started" or "task_complete" or "turn_aborted" or "token_count")) continue;
                    var key = type == "turn_context" ? type : nested!;
                    Add(key);
                    if (DateTimeOffset.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time))
                    {
                        if (dates.TryGetValue(key, out var range)) dates[key] = (time < range.Min ? time : range.Min, time > range.Max ? time : range.Max);
                        else dates[key] = (time, time);
                    }
                    else Add(key + ".missing_timestamp");
                    if (payload.TryGetProperty("turn_id", out _)) Add(key + ".turn_id_present");
                    if (payload.TryGetProperty("root_turn_id", out _)) Add(key + ".root_turn_id_present");
                    foreach (var field in new[] { "started_at", "completed_at", "duration_ms", "time_to_first_token_ms" })
                        if (payload.TryGetProperty(field, out var scalar)) Add(key + ".field." + field + "." + scalar.ValueKind);
                    if (type == "turn_context")
                    {
                        foreach (var field in new[] { "effort", "reasoning_effort", "reasoning", "model", "model_context_window", "turn_id" })
                            if (payload.TryGetProperty(field, out var value)) Add("turn_context.field." + field + "." + value.ValueKind);
                        effort = Text(payload, "effort") ?? Text(payload, "reasoning_effort");
                        if (effort is not null)
                        {
                            var safeEffort = effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ? effort : "other_encoding";
                            effortCounts[safeEffort] = effortCounts.GetValueOrDefault(safeEffort) + 1;
                        }
                    }
                    if (nested == "token_count")
                    {
                        if (effort is not null) Add("token_count.with_effort_context");
                        if (!imported.Contains(own)) Add("token_count.unimported_session");
                    }
                }
                catch (JsonException) { Add("malformed_or_partial_record"); }
            }
        }
        Console.WriteLine($"Rollout files inspected: {files.Length}; imported token session identities: {imported.Count}");
        foreach (var pair in counts) Console.WriteLine($"{pair.Key}: {pair.Value}");
        foreach (var pair in dates) Console.WriteLine($"{pair.Key} range: {pair.Value.Min:O} .. {pair.Value.Max:O}");
        foreach (var pair in effortCounts) Console.WriteLine($"effort {pair.Key}: {pair.Value}");
    }
}
