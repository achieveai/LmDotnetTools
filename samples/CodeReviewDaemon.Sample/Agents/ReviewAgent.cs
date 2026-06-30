using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Drives one collect-only review run (plan §4). Given the review input the stage executor assembled
/// from the sandbox (the PR diff plus surrounding context), it sends a single user turn through an
/// <see cref="IMultiTurnAgent"/> and collects the assistant's finalized prose into a structured result.
/// It performs NO posting and holds NO provider/sandbox wiring: it depends only on the agent interface,
/// so the executor (P4.4) owns the heavy live-loop construction while this collection logic stays
/// verifiable against a fake agent.
/// </summary>
internal sealed class ReviewAgent
{
    private readonly IMultiTurnAgent _agent;
    private readonly ILogger<ReviewAgent> _logger;

    public ReviewAgent(IMultiTurnAgent agent, ILogger<ReviewAgent> logger)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends <paramref name="reviewInput"/> as one user turn and collects the assistant's review text.
    /// </summary>
    public async Task<ReviewAgentResult> ReviewAsync(string reviewInput, CancellationToken cancellationToken)
    {
        var collected = await AgentTextCollector
            .CollectAsync(_agent, reviewInput, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Collect-only review run {RunId} produced {Count} assistant message(s), {Length} chars.",
            collected.RunId,
            collected.AssistantMessageCount,
            collected.Text.Length
        );

        return new ReviewAgentResult(collected.Text, collected.RunId);
    }
}

/// <summary>
/// The collect-only output of a review run: the assistant's assembled review text and the agent run id
/// that produced it (for correlation when the orchestrator persists the review artifact). No score or
/// verdict — grading is the Judge agent's responsibility (P4.1).
/// </summary>
internal sealed record ReviewAgentResult(string ReviewText, string? RunId);
