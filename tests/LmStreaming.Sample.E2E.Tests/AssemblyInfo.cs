// WebApplicationFactory<Program> has a known race condition when used concurrently across
// tests in the same assembly (two tests can race on the entry-point resolution, causing
// "The entry point exited without ever building an IHost"). We serialize tests here — the
// E2E suite is small and fast, so the wall-clock cost is negligible.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
