namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Stable per-user storage, independent of replaceable application binaries.</summary>
public static class AppDataLocation
{
    public static string GetDataFolder(string localAppData) =>
        Path.Combine(localAppData, "Programs", "TajemnikTV", "TajsTokens", "data");

    /// <summary>
    /// One-time copy/promotion. Caller must ensure no legacy instance is running.
    /// Original files remain recoverable; existing destination data is never merged or overwritten.
    /// </summary>
    public static string EnsureMigrated(string localAppData)
    {
        var destination = GetDataFolder(localAppData);
        if (Directory.Exists(destination)) return destination;

        var legacy = Path.Combine(localAppData, "TajsTokens");
        var staging = destination + ".migration-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
        // Never promote an incomplete snapshot. The legacy source remains the recovery copy.
        foreach (var name in new[] { "settings.json", "telemetry.db", "telemetry.db-wal", "telemetry.db-shm", "InspectionExports" })
        {
            var source = Path.Combine(legacy, name);
            if (File.Exists(source) || Directory.Exists(source))
                Copy(source, Path.Combine(staging, name));
        }
        Directory.Move(staging, destination);
        return destination;
        }
        catch
        {
            // Only our freshly-created GUID staging tree is disposable, never the source/data.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static void Copy(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to migrate a reparse point: {source}");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
                Copy(child, Path.Combine(destination, Path.GetFileName(child)));
        }
        else
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
    }
}
