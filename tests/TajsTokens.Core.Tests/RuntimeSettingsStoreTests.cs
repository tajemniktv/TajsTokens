using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class RuntimeSettingsStoreTests
{
    [Fact]
    public void Load_MissingFileReturnsSafeDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json");
        var store = new RuntimeSettingsStore(path);

        var settings = store.Load();

        Assert.True(settings.RunInBackground);
        Assert.True(settings.NotificationsEnabled);
        Assert.Equal(60, settings.PollIntervalSeconds);
        Assert.Equal([30, 20, 10, 5], settings.LowQuotaThresholds);
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
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Save_NormalizesPollingAndThresholdsAndRoundTrips()
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
                NotificationsEnabled = false
            });

            var settings = store.Load();
            Assert.Equal(15, settings.PollIntervalSeconds);
            Assert.Equal([30, 10], settings.LowQuotaThresholds);
            Assert.False(settings.NotificationsEnabled);
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
