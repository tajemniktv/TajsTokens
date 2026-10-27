using Microsoft.Data.Sqlite;
using System.Text.Json;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class RidgeFitCacheTests
{
    [Fact]
    public void ExactFitsSurviveReopenAndEveryNumericInputInvalidatesTheKey()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-ridge-").FullName;
        try
        {
            string path = Path.Combine(folder, "cache.db");
            double[][] rows = Enumerable.Range(0, 24).Select(i => new[] { (double)i, Math.Sin(i), i % 3d }).ToArray();
            double[] targets = rows.Select(x => 3 * x[0] + x[1]).ToArray();
            AccountLocalRidge expected = AccountLocalRidge.Fit(rows, targets, 10)!;
            using (var cache = new SqliteRidgeFitCache(path))
            using (RidgeFitCacheScope.Use(cache))
            {
                AssertFit(expected, AccountLocalRidge.Fit(rows, targets, 10)!);
                AssertFit(expected, AccountLocalRidge.Fit(rows, targets, 10)!);
                Assert.Equal(1, cache.Hits);
                Assert.Equal(1, cache.Misses);
            }
            using (var cache = new SqliteRidgeFitCache(path))
            using (RidgeFitCacheScope.Use(cache))
            {
                AssertFit(expected, AccountLocalRidge.Fit(rows, targets, 10)!);
                Assert.Equal(1, cache.Hits);
                Assert.Equal(0, cache.Misses);
                var changed = rows.Select(x => (double[])x.Clone()).ToArray();
                changed[0][0] = .00001;
                AccountLocalRidge.Fit(changed, targets, 10);
                AccountLocalRidge.Fit(rows, targets.Select((x, i) => i == 0 ? x + 1 : x).ToArray(), 10);
                AccountLocalRidge.Fit(rows, targets, 11);
                Assert.Equal(3, cache.Misses);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public void CorruptionIsAMissAndEntriesAreBoundedAndDefensivelyCopied()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-ridge-").FullName;
        try
        {
            string path = Path.Combine(folder, "cache.db");
            var fit = new RidgeFitParameters([1], [2], [3, 4]);
            using (var cache = new SqliteRidgeFitCache(path, maxEntries: 4))
            {
                for (int i = 0; i < 12; i++) cache.Write("key-" + i, fit);
                RidgeFitParameters found = cache.Read("key-11")!;
                found.Coefficients[0] = 999;
                Assert.Equal(3, cache.Read("key-11")!.Coefficients[0]);
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM ridge_fits_v1";
                Assert.InRange(Convert.ToInt32(command.ExecuteScalar()), 1, 4);
                command.CommandText = "UPDATE ridge_fits_v1 SET payload='{}' WHERE key='key-11';";
                command.ExecuteNonQuery();
            }
            using (var cache = new SqliteRidgeFitCache(path, maxEntries: 4))
                Assert.Null(cache.Read("key-11"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    private static void AssertFit(AccountLocalRidge expected, AccountLocalRidge actual)
    {
        Assert.Equal(expected.Means, actual.Means);
        Assert.Equal(expected.Scales, actual.Scales);
        Assert.Equal(expected.Coefficients, actual.Coefficients);
    }

    [Fact]
    public void ReachingByteLimitEvictsOldFitsAndKeepsPersistingNewFits()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-ridge-").FullName;
        try
        {
            string path = Path.Combine(folder, "cache.db");
            double[] values = Enumerable.Range(1, 160).Select(i => Math.Sqrt(i)).ToArray();
            var fit = new RidgeFitParameters(values, values, values.Append(1d).ToArray());
            using (var cache = new SqliteRidgeFitCache(path, maxEntries: 16384, maxPages: 64))
            {
                for (int i = 0; i < 200; i++) cache.Write("large-" + i, fit);
                // Once a full write frees pages, the next small entry must survive a reopen.
                cache.Write("last", new RidgeFitParameters([1], [1], [1, 1]));
            }
            Assert.InRange(new FileInfo(path).Length, 1, 64L * 4096);
            using var reopened = new SqliteRidgeFitCache(path);
            Assert.NotNull(reopened.Read("last"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public void ChronologicalReportsMatchWithoutCacheAndAfterPersistentReuse()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-ridge-").FullName;
        try
        {
            var data = ComposedQuotaEvaluatorTests.TimelyData();
            string expected = JsonSerializer.Serialize(ComposedQuotaValidation.Evaluate(data));
            string path = Path.Combine(folder, "cache.db");
            for (int pass = 0; pass < 2; pass++)
            {
                using var cache = new SqliteRidgeFitCache(path);
                using IDisposable scope = RidgeFitCacheScope.Use(cache);
                Assert.Equal(expected, JsonSerializer.Serialize(ComposedQuotaValidation.Evaluate(data)));
                Assert.True(cache.Hits > 0);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
