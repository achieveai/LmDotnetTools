// The proxy reads COPILOT_ANTHROPIC_MODEL (a process-global env var) at startup via ProxyWebAppFactory,
// so tests must not run in parallel or one factory's env value could leak into another's host boot.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
