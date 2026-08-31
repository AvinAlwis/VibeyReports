using Xunit;

// round-1 fix T1/T2: several tests in this assembly mutate the process-wide VIBEY_WORKER_PATH
// environment variable (WorkerLocatorTests) or start slow/hanging child processes with short
// timeouts (InvokeAsyncErrorPathTests). xunit parallelizes across test classes by default, and a
// stray environment-variable write from one class racing a read in another would be a source of
// flaky, hard-to-reproduce failures. Disabling parallelization keeps every test in this assembly
// deterministic at the cost of some wall-clock time, which is an acceptable trade for a suite this
// small.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
