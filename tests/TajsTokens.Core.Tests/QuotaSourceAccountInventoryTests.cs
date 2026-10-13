// Taj's Tokens | QuotaSourceAccountInventoryTests.cs
// Copyright (C) 2026 - 2026 Grzegorz Kaczmarski (TajemnikTV)
// All Rights Reserved.

using Microsoft.Data.Sqlite;
using TajsTokens.Core.Enums;
using TajsTokens.Core.Models;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class QuotaSourceAccountInventoryTests
{
    [Fact]
    public async Task RetainsDistinctHistoricalAccountsSourcesAndUnknownKeys()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TajsTokens.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        string tempRoot = Path.Combine(root.FullName, ".codex", "temp");
        Directory.CreateDirectory(tempRoot);
        string directory = Path.Combine(tempRoot, "quota-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var repository = new SqliteTelemetryRepository(Path.Combine(directory, "telemetry.db"));
            await repository.InitializeAsync(CancellationToken.None);
            DateTimeOffset first = DateTimeOffset.Parse("2026-10-01T08:00:00Z");
            QuotaSnapshot Row(DateTimeOffset at, string source, string? account) =>
                new(QuotaWindowKind.Weekly, at, 20, 10080, at.AddDays(7),
                    "codex", "default", source, account);
            await repository.UpsertQuotaSnapshotAsync(Row(first, "app-server", "account-a"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Row(first.AddHours(1), "app-server", "account-a"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Row(first.AddDays(1), "app-server", "account-b"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Row(first.AddDays(2), "rollout", "account-b"), CancellationToken.None);
            await repository.UpsertQuotaSnapshotAsync(Row(first.AddDays(3), "rollout", null), CancellationToken.None);

            QuotaSourceAccountInventory inventory = await repository.ReadQuotaSourceAccountsAsync(20, CancellationToken.None);
            Assert.False(inventory.Truncated);
            Assert.Equal(4, inventory.Observations.Count);
            QuotaSourceAccountObservation oldAccount = Assert.Single(inventory.Observations,
                row => row.Source == "app-server" && row.AccountKey == "account-a");
            Assert.Equal(2, oldAccount.ObservationCount);
            Assert.Equal(first, oldAccount.FirstObservedAtUtc);
            Assert.Equal(first.AddHours(1), oldAccount.LastObservedAtUtc);
            Assert.Contains(inventory.Observations, row => row.Source == "app-server" && row.AccountKey == "account-b");
            Assert.Contains(inventory.Observations, row => row.Source == "rollout" && row.AccountKey == "account-b");
            Assert.Contains(inventory.Observations, row => row.Source == "rollout" && row.AccountKey is null);

            QuotaSourceAccountInventory bounded = await repository.ReadQuotaSourceAccountsAsync(1, CancellationToken.None);
            Assert.True(bounded.Truncated);
            Assert.Single(bounded.Observations);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
