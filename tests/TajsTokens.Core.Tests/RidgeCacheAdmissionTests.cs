using Microsoft.Data.Sqlite;
using TajsTokens.Core.Interfaces;
using TajsTokens.Core.Services;
using TajsTokens.Infrastructure.Persistence;

namespace TajsTokens.Core.Tests;

public sealed class RidgeCacheAdmissionTests
{
    private static RidgeFitParameters Parameters(int i) => new([i], [1], [0, i]);

    [Fact]
    public void ReopenedOversizedScansRetainAStableWarmSubsetWithoutIncreasingCapacity()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-admission-").FullName;
        try
        {
            long Replay(int stride, int count = 768)
            {
                using var cache = new SqliteRidgeFitCache(Path.Combine(folder, stride + ".db"), 256, 8192, stride);
                for (int i = 0; i < count; i++)
                {
                    string key = "fit-" + i;
                    var fit = cache.Read(key);
                    if (fit is null) cache.Write(key, Parameters(i));
                    else Assert.Equal((double)i, fit.Coefficients[1]);
                }
                return cache.Hits;
            }
            // Interrupted first slice. The next full scan must reach useful saved
            // fits without its earlier misses evicting them before they can be read.
            Replay(1, 512); Replay(4, 512);
            Assert.Equal(0, Replay(1));
            Assert.InRange(Replay(4), 80, 180);
            Assert.InRange(Replay(4), 150, 250);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "4.db")};Pooling=False");
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ridge_fits_v1";
            Assert.InRange(Convert.ToInt32(command.ExecuteScalar()), 1, 256);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public void NonadmittedFitsRemainHotInMemoryAndTheDiskSubsetSurvivesReopen()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-admission-").FullName;
        try
        {
            string path = Path.Combine(folder, "cache.db");
            using (var cache = SqliteRidgeFitCache.ForCalibration(path))
            {
                for (int i = 0; i < 128; i++) cache.Write("hot-" + i, Parameters(i));
                for (int i = 0; i < 128; i++) Assert.Equal((double)i, cache.Read("hot-" + i)!.Coefficients[1]);
            }
            int[] ReadSubset()
            {
                using var cache = SqliteRidgeFitCache.ForCalibration(path);
                return Enumerable.Range(0, 128).Where(i => cache.Read("hot-" + i) is not null).ToArray();
            }
            int[] first = ReadSubset();
            Assert.InRange(first.Length, 16, 48); Assert.Equal(first, ReadSubset());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }

    [Fact]
    public void WorkerAdmissionPreservesFitValuesAndChangedTargetsCannotBorrowOldResults()
    {
        string folder = Directory.CreateTempSubdirectory("tajstokens-admission-").FullName;
        try
        {
            string path = Path.Combine(folder, "cache.db");
            double[][] rows = Enumerable.Range(0, 20).Select(i => new[] { Math.Sin(i), i / 20d }).ToArray();
            double[][] targets = Enumerable.Range(0, 32).Select(k => Enumerable.Range(0, 20).Select(i => k + Math.Cos(i)).ToArray()).ToArray();
            var expected = targets.Select(y => AccountLocalRidge.Fit(rows, y, 10)!).ToArray();
            for (int pass = 0; pass < 2; pass++)
            {
                using var cache = SqliteRidgeFitCache.ForCalibration(path);
                using var scope = RidgeFitCacheScope.Use(cache);
                for (int i = 0; i < targets.Length; i++)
                {
                    var fit = AccountLocalRidge.Fit(rows, targets[i], 10)!;
                    Assert.Equal(expected[i].Means, fit.Means); Assert.Equal(expected[i].Scales, fit.Scales);
                    Assert.Equal(expected[i].Coefficients, fit.Coefficients);
                }
                if (pass == 1)
                {
                    Assert.True(cache.Hits > 0); long misses = cache.Misses;
                    var changed = (double[])targets[0].Clone(); changed[0] += .001;
                    AccountLocalRidge.Fit(rows, changed, 10);
                    Assert.Equal(misses + 1, cache.Misses);
                }
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    }
}
