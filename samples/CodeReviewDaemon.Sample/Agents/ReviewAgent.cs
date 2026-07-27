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
    /// Sends <paramref name="reviewInput"/> as one user turn and collects the assistant's review text. When
    /// <paramref name="postEnforcementPrompt"/> is supplied (posting is authorized for this run), drives ONE
    /// more turn afterwards that makes the agent actually POST its review to the PR before we finish.
    /// <para>
    /// Why the extra turn: the review agent reliably WRITES the review but frequently SKIPS the posting step
    /// even though the prompt marks it required — observed live, run 81 (PR #208) emitted its review + notes at
    /// 17 of 150 turns and never posted. Emphatic prompt text alone did not fix it; a follow-up "you have not
    /// posted — do it now" turn does. The persisted review ARTIFACT stays the FIRST turn's text (this turn is
    /// only for the posting side-effect), and it is BEST-EFFORT: a failed enforcement turn (e.g. a
    /// context-window overflow on the larger conversation) must never discard the review we already collected.
    /// </para>
    /// </summary>
    public async Task<ReviewAgentResult> ReviewAsync(
        string reviewInput,
        string? postEnforcementPrompt,
        CancellationToken cancellationToken)
    {
        var collected = await AgentTextCollector
            .CollectAsync(_agent, reviewInput, cancellationToken)
            .ConfigureAwait(false);

        // Capture the conversation thread id the review ran on. On the in-process path this is the daemon's
        // own review-run-{id}-{variant} id; on the S2S path it is the id LmStreaming MINTED at provision (the
        // deep-link target the executor posts on the PR). Read it AFTER the run so the S2S agent has lazily
        // provisioned (its ThreadId is empty until then).
        var threadId = _agent.ThreadId;

        _logger.LogInformation(
            "Collect-only review run {RunId} produced {Count} assistant message(s), {Length} chars.",
            collected.RunId,
            collected.AssistantMessageCount,
            collected.Text.Length
        );

        if (!string.IsNullOrWhiteSpace(postEnforcementPrompt))
        {
            try
            {
                var enforced = await AgentTextCollector
                    .CollectAsync(_agent, postEnforcementPrompt, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Post-enforcement turn for run {RunId} completed ({Length} chars).",
                    enforced.RunId ?? collected.RunId,
                    enforced.Text.Length
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The review (turn 1) is the valuable artifact and is already collected; a failed enforcement
                // turn must not discard it. But a failed enforcement means the agent may NOT have posted, and with
                // the host-side summary fallback off there is no other delivery path — so surface it at Error (an
                // operational signal an operator can alert on / re-run against), not a quiet warning. Full
                // delivery verification (parse/confirm a posting receipt and re-attempt on failure) is a tracked
                // follow-up; this keeps the failure loud rather than silent.
                _logger.LogError(
                    ex,
                    "Post-enforcement turn for run {RunId} failed; the review is retained but may NOT have been "
                        + "posted to the PR — no host-side fallback will deliver it.",
                    collected.RunId
                );
            }
        }

        return new ReviewAgentResult(collected.Text, collected.RunId, threadId);
    }
}

/// <summary>
/// The collect-only output of a review run: the assistant's assembled review text, the agent run id that
/// produced it (for correlation when the orchestrator persists the review artifact), and the conversation
/// <see cref="ThreadId"/> it ran on. On the S2S path <see cref="ThreadId"/> is the LmStreaming-minted id the
/// executor turns into the posted deep-link; on the in-process path it is the daemon's own thread id. No score
/// or verdict — grading is the Judge agent's responsibility (P4.1).
/// </summary>
internal sealed record ReviewAgentResult(string ReviewText, string? RunId, string? ThreadId);
