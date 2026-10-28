using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using TajsTokens.Core.Services;

namespace TajsTokens.Core.Tests;

public sealed class WorkDiagnosticsTests
{
    [Fact]
    public void MeasurementsPairOperationsDistinguishUnknownWorkAndDoNotRepeatCompletion()
    {
        using var listener = new Listener();
        using WorkMeasurement? outer = WorkDiagnostics.Measure(WorkPhase.Collection);
        Assert.NotNull(outer);
        WorkMeasurement? inner = WorkDiagnostics.Measure(WorkPhase.TokenBatch);
        Assert.NotNull(inner);
        inner.Complete(rows: 128, committedTransactions: 1, cacheHits: 0);
        inner.Dispose(); inner.Dispose();
        outer.Dispose(); // A failed/cancelled phase still terminates, with unmeasured counters.
        object?[] completed = Assert.Single(listener.Completed, values => Convert.ToInt64(values[1]) == inner.Operation);
        Assert.Equal(1, completed[3]);
        Assert.Equal(128L, completed[4]);
        Assert.Equal(-1L, completed[5]);
        Assert.Equal(1L, completed[6]);
        object?[] incomplete = Assert.Single(listener.Completed, values => Convert.ToInt64(values[1]) == outer.Operation);
        Assert.Equal(0, incomplete[3]);
        Assert.True(Convert.ToInt64(incomplete[2]) >= 0);
        Assert.Equal(-1L, incomplete[4]);
        Assert.Contains(listener.Started, values => Convert.ToInt64(values[1]) == inner.Operation);
    }

    private sealed class Listener : EventListener
    {
        public ConcurrentQueue<object?[]> Started { get; } = new();
        public ConcurrentQueue<object?[]> Completed { get; } = new();
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "TajsTokens-Work") EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId == 1) Started.Enqueue(args.Payload!.ToArray());
            if (args.EventId == 2) Completed.Enqueue(args.Payload!.ToArray());
        }
    }
}
