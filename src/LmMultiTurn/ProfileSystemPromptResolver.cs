using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
///     Shared helper that resolves the precedence cascade for an agent's developer
///     instructions:
///     <list type="number">
///         <item><description><see cref="AgentRuntimeProfile.SystemPrompt"/> when supplied.</description></item>
///         <item><description>Constructor-supplied <c>systemPrompt</c> (e.g. base loop's <c>SystemPrompt</c>).</description></item>
///         <item><description>Provider-level <c>DeveloperInstructions</c> option.</description></item>
///     </list>
///     Centralizing the cascade keeps the Codex and Copilot loops from drifting
///     when one side fixes a bug or changes the order.
/// </summary>
internal static class ProfileSystemPromptResolver
{
    /// <summary>
    ///     Picks the highest-priority non-empty developer instructions string. Returns
    ///     <c>null</c> if all three inputs are null or whitespace.
    /// </summary>
    /// <param name="profile">
    ///     Optional <see cref="AgentRuntimeProfile"/>. Its <see cref="AgentRuntimeProfile.SystemPrompt"/>
    ///     wins when non-empty.
    /// </param>
    /// <param name="systemPrompt">
    ///     Constructor-supplied system prompt (typically the base loop's <c>SystemPrompt</c>).
    /// </param>
    /// <param name="developerInstructions">
    ///     Provider option fallback (e.g. <c>CodexSdkOptions.DeveloperInstructions</c>).
    /// </param>
    public static string? Resolve(
        AgentRuntimeProfile? profile,
        string? systemPrompt,
        string? developerInstructions)
    {
        var profileSystemPrompt = profile?.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(profileSystemPrompt))
        {
            return profileSystemPrompt;
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            return systemPrompt;
        }

        return string.IsNullOrWhiteSpace(developerInstructions) ? null : developerInstructions;
    }
}
