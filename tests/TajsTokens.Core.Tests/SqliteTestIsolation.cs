// SQLite integration fixtures clear process-wide pools during cleanup. Running collections
// concurrently races those global clears with other fixtures' live native handles on Windows.
// Keep the suite deterministic; this changes test scheduling, not application concurrency.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
