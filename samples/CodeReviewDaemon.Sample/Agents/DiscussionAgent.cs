using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Turns one round's frozen inputs into a <see cref="DiscussionDecision"/>. The seam exists so the
/// executor's policy — which interpretations it accepts, which actions it refuses — is verifiable
/// without a provider, a conversation or a sandbox anywhere near it.
/// </summary>
internal interface IDiscussionInterpreter
{
    /// <summary>Interprets <paramref name="input"/> within the caller's absolute budget.</summary>
    Task<DiscussionDecision> InterpretAsync(
        DiscussionRoundInput input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Drives the ONE turn a <c>DiscussionFollowUp</c> round gets (design §5.4).
/// <para>
/// Deliberately unlike <see cref="ReviewAgent"/>. There is no provisional turn and no completion
/// barrier, because there is no fan-out to wait on: sub-agent spawning is suppressed for the WHOLE turn,
/// which is how "the full specialist fleet is unavailable in this intent" is enforced rather than merely
/// asserted. Focused <c>Read</c>/<c>Grep</c> and provider-discussion reads are unaffected — they come
/// from the run's tool context, and suppressing spawns does not touch them.
/// </para>
/// <para>
/// The suppression scope is a REQUIRED constructor argument, where <see cref="ReviewAgent"/> accepts
/// null. That difference is the point: null there means "the diff-only path provably has no spawn
/// surface", but here a loop that cannot prove suppression is UNKNOWN, not safe — the same doctrine
/// <see cref="ReviewLoopSubAgentSurface"/> applies to the barrier. A round that cannot demonstrate the
/// fleet is off must not run at all.
/// </para>
/// <para>
/// Holds no provider or sandbox wiring: it depends only on <see cref="IMultiTurnAgent"/>, so the whole
/// class is exercised against a fake loop.
/// </para>
/// </summary>
internal sealed class DiscussionAgent : IDiscussionInterpreter
{
    private readonly IMultiTurnAgent _agent;
    private readonly ILogger<DiscussionAgent> _logger;
    private readonly Func<IDisposable> _suppressSpawning;
    private readonly TimeProvider _timeProvider;

    /// <param name="agent">The loop this round runs on.</param>
    /// <param name="logger">Where the round's outcome is named.</param>
    /// <param name="suppressSpawning">
    /// Opens a scope in which the loop refuses to start NEW sub-agents. Required — see the class remarks.
    /// </param>
    /// <param name="timeProvider">Clock used to enforce the caller's absolute budget.</param>
    public DiscussionAgent(
        IMultiTurnAgent agent,
        ILogger<DiscussionAgent> logger,
        Func<IDisposable> suppressSpawning,
        TimeProvider? timeProvider = null
    )
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _suppressSpawning =
            suppressSpawning
            ?? throw new ArgumentNullException(
                nameof(suppressSpawning),
                "a discussion round may not run on a loop that cannot prove sub-agent spawning is suppressed"
            );
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<DiscussionDecision> InterpretAsync(
        DiscussionRoundInput input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        if (_timeProvider.GetUtcNow() >= deadlineUtc)
        {
            throw new TimeoutException(
                $"The discussion round budget expired at {deadlineUtc:O}; refusing to start a turn it cannot finish."
            );
        }

        (_agent as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

        AgentTextResult collected;
        using (_suppressSpawning())
        {
            collected = await AgentTextCollector
                .CollectAsync(_agent, DiscussionFollowUpPrompt.Render(input), cancellationToken)
                .ConfigureAwait(false);
        }

        var decision = DiscussionDecisionParser.Parse(collected.Text);

        _logger.LogInformation(
            "Discussion round {RoundId} on unchanged head {HeadSha} returned relevant={IsRelevant} with "
                + "{ActionCount} proposed action(s) and {QuestionCount} question interpretation(s) (run {RunId}).",
            input.RoundId,
            input.HeadSha,
            decision.IsRelevant,
            decision.Actions.Count,
            decision.QuestionInterpretations.Count,
            collected.RunId
        );

        return decision;
    }
}
