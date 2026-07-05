using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Adapts the full <see cref="SandboxSessionRegistry"/> surface to the narrow
/// <see cref="IDiscoveredItemsSource"/> seam <see cref="DaemonReviewStageExecutor"/> depends on for
/// sub-agent discovery (Task 12), mirroring <see cref="RegistrySessionSource"/>'s adaptation of the same
/// registry for session provisioning — the executor never takes a direct dependency on the registry's
/// much larger surface and stays verifiable against a fake without a live gateway. Registered in
/// Program.cs as the DI-visible implementation of <see cref="IDiscoveredItemsSource"/>.
/// </summary>
internal sealed class RegistryDiscoverySource(SandboxSessionRegistry inner) : IDiscoveredItemsSource
{
    private readonly SandboxSessionRegistry _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<IReadOnlyList<SandboxSessionRegistry.DiscoveredItem>> ListDiscoveredAsync(string sessionId, CancellationToken ct) =>
        _inner.ListDiscoveredAsync(sessionId, ct);
}
