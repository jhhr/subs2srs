using Xunit;

// One GTK main loop per process; tests must never run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
