using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Derives the gateway agent id (<c>X-Sbx-Agent-ID</c>, gateway ADR 0040) a conversation's sub-agent
/// presents on a shared sandbox session, and maps one back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derivation:</b> <c>{subAgentId}.{conversationTag}</c>, where <c>subAgentId</c> is the
/// sub-agent's canonical id (<c>agent-7</c>, from <c>SubAgentManager</c>) and <c>conversationTag</c>
/// is the first 16 lower-case hex characters (64 bits) of SHA-256 over the UTF-8 root conversation
/// thread id. Example: <c>agent-7.3f9c0a1b2d4e5f60</c>.
/// </para>
/// <para>
/// <b>Why stable:</b> both inputs are persistent. Sub-agent ordinals are persisted per root
/// conversation and never reused, and a resumed sub-agent runs under the same id, so the gateway's
/// per-agent state (read-before-write marks, skills, PowerShell contexts) survives a sub-agent stop and
/// resume and an app restart. <b>Why the tag:</b> a sandbox session is shared by every conversation on
/// the same workspace, while <c>agent-7</c> is only unique inside one conversation.
/// </para>
/// <para>
/// A sub-agent id that is not already 1–47 characters of the gateway alphabet is replaced by
/// <c>sa-{first 16 hex of SHA-256(subAgentId)}</c>; such an id cannot be mapped back by
/// <see cref="TryGetSubAgentId"/>.
/// </para>
/// <para>
/// <b>Root agent:</b> by default it has no gateway id at all: it sends no header and lands in the
/// gateway's default bucket. A conversation that keeps its own skill tiers names its root agent with
/// <see cref="ForRoot"/> (the root thread id itself), because the default bucket is shared by every
/// conversation on the same sandbox session.
/// </para>
/// <para>
/// No cleanup on stop (product decision): a finished sub-agent's gateway state is left in place.
/// A future "retire sub-agent" action is the place to call <see cref="SandboxClient.DeleteAgentAsync"/>.
/// </para>
/// </remarks>
public static class SandboxSubAgentIdentity
{
    private const int TagLength = 16;

    // Room left for the sub-agent part: 64 - '.' - tag.
    private const int MaxSubAgentPartLength = SandboxAgentId.MaxLength - 1 - TagLength;

    /// <summary>The gateway agent id for sub-agent <paramref name="subAgentId"/> of conversation <paramref name="rootThreadId"/>.</summary>
    public static string ForSubAgent(string rootThreadId, string subAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);

        var part =
            subAgentId.Length <= MaxSubAgentPartLength && SandboxAgentId.IsValid(subAgentId)
                ? subAgentId
                : "sa-" + HexHash(subAgentId);
        return $"{part}.{HexHash(rootThreadId)}";
    }

    /// <summary>
    /// The gateway agent id for the ROOT agent of conversation <paramref name="rootThreadId"/> when it needs
    /// one of its own: the thread id itself, or <c>null</c> when the thread id is not a valid gateway agent
    /// id (the caller then has no per-conversation root id and must not act on one). The LmStreaming
    /// sample's thread ids (<c>thread-{ms}-{guid}</c>) contain no <c>.</c>, so they never take the
    /// <c>{part}.{tag}</c> form of a <see cref="ForSubAgent"/> id.
    /// </summary>
    public static string? ForRoot(string rootThreadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        return SandboxAgentId.IsValid(rootThreadId) ? rootThreadId : null;
    }

    /// <summary>
    /// The gateway agent id for the sub-agent acting on the current async flow
    /// (<see cref="AgentActorScope.Current"/>), or <c>null</c> when the flow belongs to the root agent,
    /// which must send no agent id.
    /// </summary>
    public static string? ForCurrentFlow(string rootThreadId) =>
        AgentActorScope.Current is { } actor ? ForSubAgent(rootThreadId, actor.AgentId) : null;

    /// <summary>
    /// Maps a gateway agent id back to the sub-agent id it was derived from, when it belongs to
    /// conversation <paramref name="rootThreadId"/>. False for another conversation's id, the default
    /// agent (<c>""</c>), or an id this class did not derive.
    /// </summary>
    public static bool TryGetSubAgentId(string? gatewayAgentId, string rootThreadId, out string subAgentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        subAgentId = string.Empty;
        var suffix = "." + HexHash(rootThreadId);
        if (
            gatewayAgentId is null
            || gatewayAgentId.Length <= suffix.Length
            || !gatewayAgentId.EndsWith(suffix, StringComparison.Ordinal)
        )
        {
            return false;
        }

        subAgentId = gatewayAgentId[..^suffix.Length];
        return true;
    }

    private static string HexHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..TagLength].ToLowerInvariant();
}
