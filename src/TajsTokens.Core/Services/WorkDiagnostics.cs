using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace TajsTokens.Core.Services;

public enum WorkPhase
{
    Collection = 1,
    CatalogRead = 2,
    RolloutIngestion = 3,
    PrefixVerification = 4,
    SemanticBatch = 5,
    TokenBatch = 6,
    NativeProjection = 7,
    ResetRefresh = 8,
    FleetRead = 9,
    DatasetRead = 10,
    QuotaCalibration = 11,
    WorkloadCalibration = 12,
    CalibrationSlice = 13,
    SelectedHistory = 14,
    FilesystemDiscovery = 15,
    FeatureIndexPreparation = 16,
    BaseStorageInitialization = 17,
    ObservatoryInitialization = 18,
    StateIndexInitialization = 19,
    DashboardFirstFrame = 20,
    DatasetReuse = 21,
    CalibrationEvidence = 22,
}

/// <summary>Opt-in EventPipe/ETW diagnostics. Fixed numeric phases and aggregate work
/// only: no paths, IDs, accounts, models or source payloads. Disabled by default.</summary>
[EventSource(Name = "TajsTokens-Work")]
public sealed class WorkDiagnostics : EventSource
{
    public static readonly WorkDiagnostics Log = new();
    private static long s_operation;
    private WorkDiagnostics() : base(EventSourceSettings.EtwSelfDescribingEventFormat) { }

    [NonEvent]
    public static WorkMeasurement? Measure(WorkPhase phase)
    {
        if (!Log.IsEnabled()) return null;
        var measurement = new WorkMeasurement(phase, Interlocked.Increment(ref s_operation));
        Log.PhaseStarted((int)phase, measurement.Operation);
        return measurement;
    }

    [Event(1, Level = EventLevel.Informational)]
    public void PhaseStarted(int phase, long operation)
        => WriteEvent(1, new object[] { phase, operation });

    [Event(2, Level = EventLevel.Informational)]
    public void PhaseCompleted(int phase, long operation, long elapsedMicroseconds, int succeeded,
        long rows, long logicalBytes, long committedTransactions, long cacheHits)
        => WriteEvent(2, new object[] { phase, operation, elapsedMicroseconds, succeeded,
            rows, logicalBytes, committedTransactions, cacheHits });

    // Reasons: 1 physical database change; 2 token projection change; 3 hidden history eviction.
    [Event(3, Level = EventLevel.Informational)]
    public void CacheInvalidated(int phase, int reason)
    {
        if (IsEnabled()) WriteEvent(3, new object[] { phase, reason });
    }
}

public sealed class WorkMeasurement : IDisposable
{
    private readonly WorkPhase _phase;
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _rows = -1, _bytes = -1, _commits = -1, _hits = -1;
    private bool _succeeded;
    private int _disposed;
    public long Operation { get; }
    internal WorkMeasurement(WorkPhase phase, long operation) { _phase = phase; Operation = operation; }

    // -1 means not measured in this phase, not zero work. Nested phase timings overlap.
    public void Complete(long rows = -1, long logicalBytes = -1, long committedTransactions = -1, long cacheHits = -1)
    {
        _rows = rows; _bytes = logicalBytes; _commits = committedTransactions; _hits = cacheHits;
        _succeeded = true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        long microseconds = (long)(Stopwatch.GetElapsedTime(_started).TotalMilliseconds * 1000);
        WorkDiagnostics.Log.PhaseCompleted((int)_phase, Operation, microseconds, _succeeded ? 1 : 0,
            _rows, _bytes, _commits, _hits);
    }
}
