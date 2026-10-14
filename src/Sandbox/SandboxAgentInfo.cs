namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// One agent that has made tool calls on a sandbox (gateway ADR 0040), as returned by
/// <see cref="SandboxClient.ListAgentsAsync"/>.
/// </summary>
/// <param name="AgentId">
/// The agent's id, or <c>""</c> for the default (primary, legacy shared) agent — see <see cref="IsDefault"/>.
/// </param>
/// <param name="CreatedAt">When the gateway first saw a tool call under this id on the sandbox.</param>
/// <param name="LastUsedAt">The agent's most recent tool call (durable rows are touched on a throttle).</param>
/// <param name="OpenTasks">Background tasks this agent spawned that are still running on the gateway process.</param>
/// <param name="LoadedSkills">Skills this agent currently has loaded via the <c>Skill</c> tool.</param>
public sealed record SandboxAgentInfo(
    string AgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    int OpenTasks,
    int LoadedSkills
)
{
    /// <summary>True for the default agent — the caller that sends no agent id.</summary>
    public bool IsDefault => AgentId.Length == 0;
}
