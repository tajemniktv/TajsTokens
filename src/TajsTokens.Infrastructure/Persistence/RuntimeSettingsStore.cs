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
                return new RuntimeSettings();
            }

            var json = File.ReadAllText(_path);
            return Normalize(JsonSerializer.Deserialize<RuntimeSettings>(json) ?? new RuntimeSettings());
        }
        catch
        {
            // Settings must never be able to brick the telemetry shell. A corrupt or inaccessible
            // file falls back to safe defaults and can be replaced by the next successful save.
            return new RuntimeSettings();
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
        var thresholds = (settings.LowQuotaThresholds ?? [])
            .Where(value => value is > 0 and < 100)
            .Distinct()
            .OrderByDescending(value => value)
            .ToArray();

        return settings with
        {
            PollIntervalSeconds = Math.Clamp(settings.PollIntervalSeconds, 15, 3600),
            LowQuotaThresholds = thresholds.Length == 0 ? [30, 20, 10, 5] : thresholds
        };
    }
}
