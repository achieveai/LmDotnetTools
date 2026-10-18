namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The gateway's per-agent identity inside one sandbox (gateway ADR 0040): a caller-chosen id that
/// partitions caller history — read-before-write freshness, loaded skills, background tasks,
/// PowerShell contexts, discovery notifications and the default browser — between the main agent and
/// its sub-agents on a shared sandbox session.
/// </summary>
/// <remarks>
/// <para>
/// A valid id is 1–64 characters of <c>[A-Za-z0-9._-]</c> (gateway <c>gateway-types/src/agent_id.rs</c>).
/// The primary (default) agent has NO id: callers select it by <b>omitting</b> the
/// <see cref="HeaderName"/> header, never by sending it empty — the gateway rejects an empty or invalid
/// value with JSON-RPC <c>-32602</c> instead of falling back to the default.
/// </para>
/// <para>
/// Agent ids are a cooperative partition within a sandbox the caller already owns, not a security
/// boundary. There is no create call: the first tool call under an id creates that agent.
/// </para>
/// </remarks>
public static class SandboxAgentId
{
    /// <summary>The request header the gateway reads the calling agent's id from.</summary>
    public const string HeaderName = "X-Sbx-Agent-ID";

    /// <summary>Maximum id length in characters (the alphabet is ASCII, so also bytes).</summary>
    public const int MaxLength = 64;

    /// <summary>True when <paramref name="agentId"/> is a valid, non-default agent id.</summary>
    public static bool IsValid(string? agentId) =>
        agentId is { Length: > 0 and <= MaxLength }
        && agentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless <paramref name="agentId"/> is a valid, non-default
    /// agent id. An empty value is refused too: the default agent is selected by sending no id at all.
    /// </summary>
    public static void ThrowIfInvalid(string agentId, string paramName)
    {
        ArgumentNullException.ThrowIfNull(agentId, paramName);
        if (!IsValid(agentId))
        {
            throw new ArgumentException(
                $"Agent ID must contain 1–{MaxLength} letters, digits, dots, underscores or hyphens; "
                    + "omit it (null) to act as the default agent.",
                paramName
            );
        }
    }
}
