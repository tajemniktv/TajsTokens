using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class AppDataLocationTests
{
    [Fact]
    public void MigrationPreservesLegacyAndDoesNotMergeIntoExistingData()
    {
        var root = CreateRoot();
        try
        {
            var legacy = Path.Combine(root, "TajsTokens");
            Directory.CreateDirectory(Path.Combine(legacy, "InspectionExports"));
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "settings");
            File.WriteAllText(Path.Combine(legacy, "telemetry.db"), "db");
            File.WriteAllText(Path.Combine(legacy, "telemetry.db-wal"), "wal");
            File.WriteAllText(Path.Combine(legacy, "InspectionExports", "local.txt"), "export");
            var data = AppDataLocation.EnsureMigrated(root);
            Assert.Equal(Path.Combine(root, "Programs", "TajemnikTV", "TajsTokens", "data"), data);
            Assert.Equal("settings", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.Equal("db", File.ReadAllText(Path.Combine(legacy, "telemetry.db")));
            Assert.Equal("wal", File.ReadAllText(Path.Combine(data, "telemetry.db-wal")));
            Assert.Equal("export", File.ReadAllText(Path.Combine(data, "InspectionExports", "local.txt")));
            File.WriteAllText(Path.Combine(data, "settings.json"), "new settings");
            AppDataLocation.EnsureMigrated(root);
            Assert.Equal("new settings", File.ReadAllText(Path.Combine(data, "settings.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FailedMigrationNeverPromotesPartialDataAndCanRetry()
    {
        var root = CreateRoot();
        try
        {
            var legacy = Directory.CreateDirectory(Path.Combine(root, "TajsTokens")).FullName;
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "settings");
            var db = Path.Combine(legacy, "telemetry.db");
            File.WriteAllText(db, "db");
            using (var held = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => AppDataLocation.EnsureMigrated(root));
            Assert.False(Directory.Exists(AppDataLocation.GetDataFolder(root)));
            var data = AppDataLocation.EnsureMigrated(root);
            Assert.Equal("db", File.ReadAllText(Path.Combine(data, "telemetry.db")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TajsTokens.slnx"))) repo = repo.Parent;
        var root = Path.Combine(repo?.FullName ?? throw new InvalidOperationException("Repository not found"),
            ".codex", "temp", "data-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
