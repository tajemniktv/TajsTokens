using System.Text.Json;
using TajsTokens.Core.Models;

namespace TajsTokens.Infrastructure.Persistence;

public sealed class RuntimeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
        var normalized = Normalize(settings);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temp, _path, overwrite: true);
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
