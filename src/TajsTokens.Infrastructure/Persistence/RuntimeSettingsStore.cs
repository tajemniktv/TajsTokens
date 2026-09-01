using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed class RuntimeSettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public RuntimeSettingsStore(string path)
    {
        _path = path;
    }

    public RuntimeSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                var defaults = Normalize(new RuntimeSettings());
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_path);
            return Normalize(JsonSerializer.Deserialize<RuntimeSettings>(json) ?? new RuntimeSettings());
        }
        catch
        {
            // Settings must never be able to brick the telemetry shell. A corrupt, inaccessible, or
            // future-version file falls back to safe in-memory defaults. In particular, do not
            // rewrite an unsupported newer schema merely because an older binary happened to start.
            return Normalize(new RuntimeSettings());
        }
    }

    public void Save(RuntimeSettings settings)
    {
        var persistedVersion = ReadPersistedSchemaVersion();
        if (persistedVersion > RuntimeSettings.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Persisted runtime settings schema {persistedVersion} is newer than supported version {RuntimeSettings.CurrentSchemaVersion}; refusing to overwrite it.");
        }

        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(normalized, s_jsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private int? ReadPersistedSchemaVersion()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (!document.RootElement.TryGetProperty(nameof(RuntimeSettings.SchemaVersion), out var property))
            {
                return 0;
            }

            return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var version)
                ? version
                : 0;
        }
        catch (JsonException)
        {
            // A corrupt current-version file may be replaced by an explicit successful save. Only a
            // parseable future schema is protected from older binaries.
            return null;
        }
    }

    internal static RuntimeSettings Normalize(RuntimeSettings settings)
    {
        if (settings.SchemaVersion > RuntimeSettings.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Runtime settings schema {settings.SchemaVersion} is newer than supported version {RuntimeSettings.CurrentSchemaVersion}.");
        }

        return settings with
        {
            SchemaVersion = RuntimeSettings.CurrentSchemaVersion,
            PollIntervalSeconds = Math.Clamp(settings.PollIntervalSeconds, 15, 3600),
            LowQuotaThresholds = RuntimeSettings.NormalizeLowQuotaThresholds(settings.LowQuotaThresholds)
        };
    }
}
