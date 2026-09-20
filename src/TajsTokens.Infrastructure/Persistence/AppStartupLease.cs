// Taj's Tokens | AppStartupLease.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

namespace TajsTokens.Infrastructure.Persistence;

/// <summary>Serializes ordinary startup/migration with deployment's stopped-app transaction.</summary>
public static class AppStartupLease
{
    public static FileStream? TryAcquire(string localAppData)
    {
        string root = Path.GetDirectoryName(AppDataLocation.GetDataFolder(localAppData))!;
        Directory.CreateDirectory(root);
        try
        {
            return new FileStream(Path.Combine(root, "startup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}