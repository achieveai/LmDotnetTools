using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The hardcoded sub-agent template catalog shared by every provider path that supports
/// sub-agent orchestration: the real middleware providers (via
/// <c>Program.BuildProductionSubAgentOptionsAsync</c>) and the test providers (via
/// <see cref="DefaultTestAgentBuilder"/>). Centralising the definitions keeps the two paths from
/// drifting, so the <c>Agent</c> tool advertises the same built-in types regardless of provider.
/// </summary>
internal static class BuiltInSubAgentTemplates
{
    /// <summary>
    /// Default concurrent sub-agent cap applied wherever these templates are wrapped in a
    /// <see cref="SubAgentOptions"/>. Bounds how many sub-agents the <c>Agent</c> tool can run in
    /// parallel before an additional spawn waits (up to 5s) for a slot — each concurrent sub-agent
    /// is a live provider/LLM call, so this is also the concurrent-request fan-out per conversation.
    /// Scaled to the host like the workflow engine: <c>min(16, cores - 2)</c>, floored at 1 so a
    /// low-core/CI host can never produce an invalid <c>SemaphoreSlim(0)</c>. On a 16-core box = 14.
    /// </summary>
    internal static readonly int DefaultMaxConcurrentSubAgents =
        Math.Max(1, Math.Min(16, Environment.ProcessorCount - 2));

    /// <summary>
    /// Builds a fresh dictionary of the built-in templates. Each template reuses
    /// <paramref name="providerAgentFactory"/> — invoked per spawn so every sub-agent gets a
    /// FRESH provider agent on the same backend as the parent.
    /// </summary>
    public static Dictionary<string, SubAgentTemplate> Create(Func<IStreamingAgent> providerAgentFactory)
    {
        ArgumentNullException.ThrowIfNull(providerAgentFactory);

        return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["general-purpose"] = new SubAgentTemplate
            {
                Name = "General-purpose agent",
                Description = "Autonomous worker for multi-step tasks: research, search, and follow-through.",
                WhenToUse =
                    "Delegate self-contained tasks that need several tool calls or focused investigation "
                    + "so the parent context stays clean. Not for trivial one-shot answers.",
                SystemPrompt =
                    "You are a general-purpose sub-agent working on behalf of a parent agent. "
                    + "Complete the delegated task end to end using the tools available to you, then "
                    + "return a concise final answer that fully captures your findings — the parent only "
                    + "sees your final message, not your intermediate steps.",
                AgentFactory = providerAgentFactory,
                MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
            },
            ["researcher"] = new SubAgentTemplate
            {
                Name = "Researcher",
                Description = "Focused investigator that gathers information and summarizes findings.",
                WhenToUse =
                    "Delegate open-ended investigation across sources when you need a distilled summary "
                    + "rather than raw results. Prefer general-purpose for tasks that also mutate state.",
                SystemPrompt =
                    "You are a research sub-agent. Investigate the delegated question thoroughly using the "
                    + "tools available to you, cross-check what you find, and return a clear, well-structured "
                    + "summary. The parent only sees your final message, so make it self-contained.",
                AgentFactory = providerAgentFactory,
                MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
            },
        };
    }
}
