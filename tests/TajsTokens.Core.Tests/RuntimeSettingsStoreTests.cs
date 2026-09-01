using System.Text.Json;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class RuntimeSettingsStoreTests
{
    [Fact]
    public void Load_MissingFileCreatesAndReturnsSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new RuntimeSettingsStore(path);

        try
        {
            var settings = store.Load();

            Assert.True(settings.RunInBackground);
            Assert.True(settings.NotificationsEnabled);
            Assert.Equal(60, settings.PollIntervalSeconds);
            Assert.Equal([30, 20, 10, 5], settings.LowQuotaThresholds);
            Assert.False(settings.TokscaleReconciliationEnabled);
            Assert.False(settings.TokscaleFallbackEnabled);
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.True(File.Exists(path));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_CorruptFileReturnsSafeDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{ nope");

        try
        {
            var settings = new RuntimeSettingsStore(path).Load();
            Assert.Equal(60, settings.PollIntervalSeconds);
            Assert.True(settings.RunInBackground);
            Assert.False(settings.TokscaleReconciliationEnabled);
            Assert.False(settings.TokscaleFallbackEnabled);
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_VersionlessFileNormalizesToCurrentSchemaAndKeepsTokscaleOptional()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, """
            {
              "RunInBackground": false,
              "PollIntervalSeconds": 120,
              "NotificationsEnabled": true,
              "LowQuotaThresholds": [20, 10],
              "LaunchAtLogin": false
            }
            """);

        try
        {
            var settings = new RuntimeSettingsStore(path).Load();
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.False(settings.RunInBackground);
            Assert.Equal(120, settings.PollIntervalSeconds);
            Assert.Equal([20, 10], settings.LowQuotaThresholds);
            Assert.False(settings.TokscaleReconciliationEnabled);
            Assert.False(settings.TokscaleFallbackEnabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_NewerSchemaReturnsDefaultsAndBlocksOrdinaryOverwrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var original = """
            {
              "SchemaVersion": 999,
              "RunInBackground": false,
              "PollIntervalSeconds": 777
            }
            """;
        File.WriteAllText(path, original);
        var store = new RuntimeSettingsStore(path);

        try
        {
            var settings = store.Load();
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.True(settings.RunInBackground);
            Assert.Equal(60, settings.PollIntervalSeconds);
            Assert.Equal(original, File.ReadAllText(path));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                store.Save(settings with { NotificationsEnabled = false }));
            Assert.Contains("refusing to overwrite", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_NormalizesAndRoundTripsOptionalTokscaleSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new RuntimeSettingsStore(path);

        try
        {
            store.Save(new RuntimeSettings
            {
                PollIntervalSeconds = 1,
                LowQuotaThresholds = [10, 30, 10, -1, 500],
                NotificationsEnabled = false,
                TokscaleReconciliationEnabled = true,
                TokscaleFallbackEnabled = true
            });

            var settings = store.Load();
            Assert.Equal(RuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(15, settings.PollIntervalSeconds);
            Assert.Equal([30, 10], settings.LowQuotaThresholds);
            Assert.False(settings.NotificationsEnabled);
            Assert.True(settings.TokscaleReconciliationEnabled);
            Assert.True(settings.TokscaleFallbackEnabled);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_RejectsNewerSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new RuntimeSettingsStore(path);

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                store.Save(new RuntimeSettings { SchemaVersion = RuntimeSettings.CurrentSchemaVersion + 1 }));
            Assert.Contains("newer than supported", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
