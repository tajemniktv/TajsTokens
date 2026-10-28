using TajsTokens.Core.Services;

namespace TajsTokens.Infrastructure.Persistence;

internal static class SqliteMemoryDiagnostics
{
    public static void Record(WorkPhase phase)
    {
        if (!WorkDiagnostics.Log.IsEnabled()) return;
        long allocated = -1, highWater = -1;
        try
        {
            allocated = SQLitePCL.raw.sqlite3_memory_used();
            highWater = SQLitePCL.raw.sqlite3_memory_highwater(0); // Observe; never reset another owner's statistics.
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // Optional diagnostics must not turn an unavailable native provider into a new failure.
        }
        WorkDiagnostics.RecordMemory(phase, allocated, highWater);
    }
}
