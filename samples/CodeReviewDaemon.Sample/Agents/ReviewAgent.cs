using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// A review loop that can be bound to an absolute deadline supplied by its caller. Implemented by
/// <see cref="S2SReviewAgent"/>, whose poll windows would otherwise restart per turn: collect → barrier →
/// synthesize share ONE budget, so a second turn must not open a fresh window with most of that budget
/// already spent. Loops that finish when the provider finishes (the in-process path) do not implement it.
/// <para>
/// The deadline is pushed rather than passed on <c>ExecuteRunAsync</c> because it is an attribute of the
/// review ATTEMPT, not of the <see cref="IMultiTurnAgent"/> contract — keeping it off that interface is what
/// lets the daemon express the shared budget without every loop implementation growing a parameter it
/// ignores.
/// </para>
/// </summary>
internal interface IDeadlineBoundedReviewLoop
{
    /// <summary>Binds the next turn(s) to the absolute <paramref name="deadlineUtc"/>.</summary>
    void UseDeadline(DateTimeOffset deadlineUtc);
}

/// <summary>
/// Drives ONE review conversation through its two phases (plan §4, recursive-review completion barrier):
/// <list type="number">
/// <item><see cref="CollectProvisionalAsync"/> — one collect-only turn over the review input the stage
///   executor assembled. Its answer is PROVISIONAL: the parent may still have sub-agents in flight, so the
///   answer is never posted, judged, or persisted as authoritative. There is no posting/enforcement
///   parameter, so a collect-only turn is structurally all this phase can ever be.</item>
/// <item><see cref="SynthesizeFinalAsync"/> — the authoritative turn, driven by the executor AFTER the
///   sub-agent completion barrier opened, on the SAME agent and therefore the same conversation. Sub-agent
///   spawning is suppressed for its duration (the children have settled; starting new ones would reopen the
///   barrier it just waited on) while reading what they delivered stays available.</item>
/// </list>
/// Both phases share the caller's ONE absolute deadline; neither invents a window of its own. The class
/// performs NO posting and holds NO provider/sandbox wiring: it depends only on
/// <see cref="IMultiTurnAgent"/>, so the executor owns the heavy live-loop construction while this
/// two-phase logic stays verifiable against a fake agent.
/// </summary>
internal sealed class ReviewAgent
{
    private readonly IMultiTurnAgent _agent;
    private readonly ILogger<ReviewAgent> _logger;
    private readonly Func<IDisposable>? _suppressSpawning;

    /// <summary>
    /// Builds the agent. <paramref name="suppressSpawning"/> opens a scope in which the underlying loop
    /// refuses to start NEW sub-agents; it is a delegate rather than the SDK's provider type so no
    /// <c>LmMultiTurn.SubAgents</c> type leaks into this class and a test can supply a recording lambda.
    /// <c>null</c> (the S2S and diff-only paths) means there is no in-process spawn surface to suppress.
    /// </summary>
    public ReviewAgent(
        IMultiTurnAgent agent,
        ILogger<ReviewAgent> logger,
        Func<IDisposable>? suppressSpawning = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _suppressSpawning = suppressSpawning;
    }

    /// <summary>
    /// Sends <paramref name="input"/> as one user turn and collects the assistant's provisional review text.
    /// Tolerates a blank answer: this turn is never the authoritative review, so an empty provisional is a
    /// fact about a parent that deferred everything to its children, not a failure.
    /// </summary>
    public async Task<ReviewAgentResult> CollectProvisionalAsync(
        string input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var result = await RunTurnAsync(input, deadlineUtc, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Provisional review turn {RunId} produced {Length} chars on thread {ThreadId}; children may still be running.",
            result.RunId,
            result.ReviewText.Length,
            result.ThreadId);
        return result;
    }

    /// <summary>
    /// Drives the authoritative synthesis turn on the SAME agent/conversation, with sub-agent spawning
    /// suppressed for exactly this turn. <paramref name="allowInlinePosting"/> states whether THIS agent was
    /// asked to deliver the review to the PR itself (in-process path) or whether delivery happens elsewhere
    /// (the S2S host publishes it), and only shapes the failure wording an operator reads — it gates no
    /// tools, which come from the run's tool context.
    /// <para>
    /// A generation failure or a blank answer THROWS: unlike the provisional turn there is no earlier
    /// authoritative answer to fall back on, so promoting an empty artifact would silently publish "no
    /// review". Provider VERIFICATION (did the comment actually land?) deliberately stays outside this
    /// method so its failures remain eligible for the caller's fallback handling.
    /// </para>
    /// </summary>
    public async Task<ReviewAgentResult> SynthesizeFinalAsync(
        string synthesisPrompt,
        bool allowInlinePosting,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(synthesisPrompt);

        ReviewAgentResult result;
        using (_suppressSpawning?.Invoke())
        {
            result = await RunTurnAsync(synthesisPrompt, deadlineUtc, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(result.ReviewText))
        {
            throw new InvalidOperationException(
                $"Synthesis turn {result.RunId} on thread {result.ThreadId} produced no review text; there is "
                    + (allowInlinePosting
                        ? "nothing authoritative to persist and the agent was expected to post it inline."
                        : "nothing authoritative to persist or hand to the publisher."));
        }

        _logger.LogInformation(
            "Synthesis turn {RunId} produced the authoritative review ({Length} chars, inline posting {InlinePosting}).",
            result.RunId,
            result.ReviewText.Length,
            allowInlinePosting);
        return result;
    }

    /// <summary>
    /// Runs one turn under the shared budget: refuses to start a turn the budget can no longer cover, binds
    /// a deadline-bounded loop to it, then collects the finalized assistant prose. The thread id is read
    /// AFTER the run because the S2S agent provisions lazily (its <c>ThreadId</c> is empty until then).
    /// </summary>
    private async Task<ReviewAgentResult> RunTurnAsync(
        string input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow >= deadlineUtc)
        {
            throw new TimeoutException(
                $"The review budget expired at {deadlineUtc:O}; refusing to start a turn it cannot finish.");
        }

        (_agent as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

        var collected = await AgentTextCollector
            .CollectAsync(_agent, input, cancellationToken)
            .ConfigureAwait(false);

        return new ReviewAgentResult(collected.Text, collected.RunId, _agent.ThreadId);
    }
}

/// <summary>
/// The collect-only output of one review turn: the assistant's assembled text, the agent run id that
/// produced it (for correlation when the orchestrator persists the review artifact), and the conversation
/// <see cref="ThreadId"/> it ran on. On the S2S path <see cref="ThreadId"/> is the LmStreaming-minted id the
/// executor turns into the posted deep-link; on the in-process path it is the daemon's own thread id. No score
/// or verdict — grading is the Judge agent's responsibility (P4.1).
/// </summary>
internal sealed record ReviewAgentResult(string ReviewText, string? RunId, string? ThreadId);
