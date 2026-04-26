// The Browser E2E suite boots a real Kestrel host (BrowserWebAppFactory) and drives it with
// Playwright. Two factors force serialized execution:
//   1. LM_PROVIDER_MODE is read once at Program.cs startup and is process-global, so two
//      factories in flight would race on the env var.
//   2. Chromium launches are heavy; running them in parallel on CI hosts causes flakes.
// Tests are fast individually, so the wall-clock cost of serializing is negligible.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
