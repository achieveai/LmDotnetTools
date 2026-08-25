using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the live <see cref="IMultiTurnAgent"/> loop a daemon agent (Review / Judge / Knowledge / B
/// variant) drives for one collect-only run. This is the single seam between the declarative
/// <see cref="AgentProfile"/> (built by <see cref="DaemonAgentFactory"/>) and the concrete provider
/// loop, so the stage executor's agent logic stays verifiable against a fake while the real provider
/// wiring lives in <see cref="S2SReviewAgentLoopFactory"/>.
/// </summary>
internal interface IReviewAgentLoopFactory
{
    /// <summary>
    /// Creates a fresh agent loop for <paramref name="profile"/> on its own conversation
    /// <paramref name="threadId"/>, overriding the model with <paramref name="modelId"/> when supplied.
    /// <paramref name="reasoningEffort"/> sets the adaptive-thinking effort (<c>output_config.effort</c>)
    /// for this loop; <c>null</c> uses the daemon's configured default and an empty value omits it
    /// entirely (required for non-adaptive models — e.g. Copilot's haiku rejects an effort it does not
    /// support). <paramref name="toolContext"/> is <c>null</c> on the diff-only path (today's behavior:
    /// empty tool registry, no sub-agents); when supplied it connects the gateway MCP client filtered to
    /// its read-only allow-list and attaches any configured sub-agents. <paramref name="reviewWorkspace"/>
    /// is the prepared LmStreaming workspace the S2S factory provisions the hosted review conversation
    /// against (minted by <see cref="S2SReviewWorkspacePreparer"/>) — the whole record, not just its id,
    /// because the factory also titles the conversation from the PR it was prepared for. It is <c>null</c>
    /// — and ignored — on paths that own the conversation locally (test fakes). <paramref name="resumeHostedThreadId"/> rejoins an
    /// ALREADY-PROVISIONED hosted conversation instead of minting a new one: a review is two
    /// turns — collect-only provisional, then authoritative synthesis after the sub-agent completion
    /// barrier — and one picked up after a restart must continue on the thread its provisional turn ran on,
    /// which is also the deep-link already posted on the PR. The caller owns the returned loop's lifetime
    /// (it is <see cref="IAsyncDisposable"/>).
    /// </summary>
    IMultiTurnAgent Create(
        AgentProfile profile,
        string? modelId,
        string threadId,
        string? reasoningEffort = null,
        ReviewToolContext? toolContext = null,
        PreparedReviewWorkspace? reviewWorkspace = null,
        string? resumeHostedThreadId = null);

    /// <summary>
    /// What <see cref="Create"/> will ACTUALLY run on given <paramref name="requestedModelId"/> — which is
    /// not always what was asked for. The S2S factory discards the per-call id (provision carries no model
    /// field), so a caller that wants to record or reason about the model has to ask instead of assume.
    /// <para>
    /// Returns <c>null</c> when the transport exposes no model identity at all. Null is <b>unknown</b>, not
    /// a value: a caller comparing two nulls must not conclude the two runs shared a model, and one
    /// persisting a null must not present it as a measurement. Anything a caller records from this is a
    /// claim about production, so a factory must never return an id it will not honour.
    /// </para>
    /// </summary>
    string? ResolveEffectiveModelId(string? requestedModelId);
}
