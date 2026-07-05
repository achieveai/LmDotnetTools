using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Adapts the full <see cref="SandboxSessionRegistry"/> surface to the narrow
/// <see cref="ISandboxSessionSource"/> seam <see cref="ReviewSessionProvisioner"/> depends on, so the
/// provisioner never takes a direct dependency on the registry's much larger surface (sub-agent
/// bindings, discovery, thread routing, …) and stays verifiable against a fake without a live gateway.
/// Registered in Program.cs (a later integration task) as the DI-visible implementation of
/// <see cref="ISandboxSessionSource"/>.
/// </summary>
internal sealed class RegistrySessionSource(SandboxSessionRegistry inner) : ISandboxSessionSource
{
    private readonly SandboxSessionRegistry _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct) =>
        _inner.GetOrCreateLiveSessionAsync(workspaceRef, ct);

    public Task DestroyWorkspaceSessionAsync(string workspaceId, CancellationToken ct) =>
        _inner.DestroyWorkspaceSessionAsync(workspaceId, ct);
}
