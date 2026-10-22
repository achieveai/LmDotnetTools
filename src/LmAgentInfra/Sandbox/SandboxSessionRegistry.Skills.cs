using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Credential-routed pass-throughs to the gateway's per-agent skill tiers (gateway ADR 0041). Each call
/// resolves the session's own credential, like every other control-plane call on this registry.
/// </summary>
public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// One agent's skill catalog for <paramref name="sessionId"/>. <paramref name="agentId"/> null or
    /// <c>""</c> reads the DEFAULT agent's catalog: every admitted skill, with the default agent's tiers.
    /// Propagates <see cref="SandboxException"/>.
    /// </summary>
    public Task<SandboxSkillCatalog> GetSkillCatalogAsync(
        string sessionId,
        string? agentId = null,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return ClientFor(CredentialFor(sessionId)).GetSkillCatalogAsync(sessionId, agentId, ct: ct);
    }

    /// <summary>
    /// Sets skill tiers for ONE named agent. Fails closed on the default agent: the SDK maps a null or
    /// <c>""</c> agent id to the default agent, which every conversation on a shared session lands in, so
    /// this refuses any id that is not a valid gateway agent id before a request is built.
    /// Propagates <see cref="SandboxException"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="agentId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is empty or not a valid gateway agent id.</exception>
    public Task<SandboxSkillCatalog> PatchSkillTiersAsync(
        string sessionId,
        string agentId,
        IReadOnlyDictionary<string, SandboxSkillTier> set,
        bool reset,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
        ArgumentNullException.ThrowIfNull(set);
        return ClientFor(CredentialFor(sessionId)).PatchSkillTiersAsync(sessionId, agentId, set, reset, ct);
    }
}
