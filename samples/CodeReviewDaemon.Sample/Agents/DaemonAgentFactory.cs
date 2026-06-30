using AchieveAi.LmDotnetTools.LmAgentInfra;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the daemon's <see cref="AgentProfile"/> records declaratively (plan §4). A profile is a
/// pure value — identity, system prompt, and tool gating — so this factory is fully unit-testable and
/// carries no provider/sandbox wiring. The live agent loop (provider client + MCP tools) is assembled
/// by the orchestrator's stage executor, which feeds the profile produced here into the shared
/// <see cref="Agents"/> pool; keeping construction out of the profile is what lets the profile stay a
/// plain declaration with no inheritance or runtime override.
/// </summary>
internal static class DaemonAgentFactory
{
    /// <summary>Stable id of the review profile (used for logging and agent recreation).</summary>
    public const string ReviewProfileId = "review";

    private const string ReviewSystemPrompt = """
        You are an unattended code-review agent reviewing a single pull request.

        You are given the PR's diff and surrounding context. Produce one focused review:
        - Call out correctness bugs, security issues, and contract/compatibility breaks first.
        - Then note maintainability and test-coverage concerns.
        - Tag each finding with a severity (Must / Should / Consider) and cite the file and line.
        - If the change looks sound, say so plainly rather than inventing nitpicks.

        Write the review as Markdown. Do not attempt to post comments, push commits, or otherwise act
        on the repository — your output is collected by the daemon, which owns all posting.
        """;

    /// <summary>
    /// Builds the review-agent profile. The reviewer needs no provider built-in tools (it reasons over
    /// the diff the executor supplies), so <see cref="AgentProfile.EnabledBuiltInTools"/> is empty; the
    /// MCP tool allow-list (<see cref="AgentProfile.EnabledTools"/>) is left to the capability-enforcing
    /// executor, which is the layer that knows the concrete sandbox tool names.
    /// </summary>
    public static AgentProfile CreateReviewProfile() =>
        new(
            Id: ReviewProfileId,
            Name: "Review Agent",
            SystemPrompt: ReviewSystemPrompt,
            EnabledTools: null,
            EnabledBuiltInTools: []);

    /// <summary>
    /// Builds the review profile for one A/B <paramref name="variant"/>. The variant's
    /// <see cref="ReviewVariant.SystemPrompt"/> becomes the profile's prompt — the prompt/skill axis of
    /// the comparison — while tool gating stays identical to <see cref="CreateReviewProfile"/> (no
    /// built-ins; MCP allow-list deferred to the executor). The <see cref="ReviewVariant.ModelId"/> and
    /// the <see cref="ReviewVariant.CanWrite"/> capability are applied by the executor, not the profile,
    /// so the profile stays a plain declaration.
    /// </summary>
    public static AgentProfile CreateVariantProfile(ReviewVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant.SystemPrompt);

        return new AgentProfile(
            Id: $"{ReviewProfileId}-{variant.VariantId}",
            Name: $"Review Agent ({variant.VariantId})",
            SystemPrompt: variant.SystemPrompt,
            EnabledTools: null,
            EnabledBuiltInTools: []);
    }
}
