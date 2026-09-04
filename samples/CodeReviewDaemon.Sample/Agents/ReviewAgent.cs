using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Persistence.Models;

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
/// A review loop whose turn is durable on a HOST that outlives the daemon process: the host records the turn
/// as an accepted input before it starts producing, so the daemon can checkpoint that input id and, after a
/// restart, rejoin the very same in-flight turn instead of queueing a second one on the same conversation.
/// Implemented by <see cref="S2SReviewAgent"/> only — an in-process turn dies with the loop that started it,
/// so there is nothing to rejoin and it deliberately declares nothing here rather than fabricating an id.
/// <para>
/// Pushed like <see cref="IDeadlineBoundedReviewLoop.UseDeadline"/>, and for the same reason: resumability is
/// an attribute of the review ATTEMPT, not of the <see cref="IMultiTurnAgent"/> contract. Returning the id
/// from the turn would be useless — the checkpoint has to exist BEFORE the minutes-long wait it protects, not
/// after it.
/// </para>
/// </summary>
internal sealed record ConversationProvisionObserver(Action<string> Associate, Action Retract);

internal interface IResumableReviewTurn
{
    /// <summary>
    /// Registers a callback invoked synchronously immediately before a new hosted conversation is provisioned.
    /// The callback must durably record that provisioning may begin and returns terminal first-write callbacks:
    /// associate the exact parsed thread id, or retract only when the host proves it refused before minting.
    /// If recording fails, provisioning must not begin. A resumed thread invokes neither callback.
    /// </summary>
    void ObserveConversationProvision(Func<ConversationProvisionObserver> onConversationProvisioning);

    /// <summary>
    /// Registers a callback invoked the instant a hosted conversation is MINTED for this loop — before the
    /// first turn is sent, and therefore before any sub-agent fan-out exists.
    /// <para>
    /// This is the narrowest window in the whole lifecycle and the one that matters most: a daemon that dies
    /// between the mint and the end of the (minutes-long) provisional turn has already started a sub-agent
    /// tree it can no longer find, and would start a second one on a second conversation. Checkpointing here
    /// is what makes that window recoverable.
    /// </para>
    /// <para>
    /// Unlike a bookkeeping hook this one is load-bearing, so implementations must let it FAIL the mint: a
    /// conversation whose identity could not be recorded is precisely the orphan this exists to prevent.
    /// </para>
    /// </summary>
    void ObserveConversationMint(Action<string> onConversationMinted);

    /// <summary>
    /// Arms the NEXT turn's checkpointing, one shot (a later turn on the same loop is unarmed again, so a
    /// spent input can never be rejoined twice).
    /// <para>
    /// A non-null <paramref name="acceptedInputId"/> makes that turn REJOIN an input the host already
    /// accepted: it is polled to completion and never re-sent, because the host is already producing an
    /// answer for it and a second send would run the same turn twice on one conversation.
    /// </para>
    /// <para>
    /// Otherwise the turn is SENT under <paramref name="idempotencyKey"/> — which the caller derives from
    /// durable state so it is identical on every attempt at the same turn — and
    /// <paramref name="onInputAccepted"/>, when supplied, is invoked with the accepted id the instant the host
    /// takes it, before any polling. The key is what closes the lost-response window the callback cannot: a
    /// daemon that dies after the host accepted the send but before the response arrived has no id to
    /// checkpoint, and re-sending under the same key returns that same input instead of queueing a second turn.
    /// A caller whose recovery rests on the key alone therefore passes no callback.
    /// </para>
    /// </summary>
    void ArmTurnCheckpoint(string idempotencyKey, string? acceptedInputId, Action<string>? onInputAccepted);
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
    /// <c>null</c> means there is no spawn surface to suppress at all (the diff-only path). The S2S path is
    /// NOT null: <see cref="S2SReviewAgent"/> supplies a scope that carries the suppression over the wire to
    /// the hosted loop, which enforces it there.
    /// </summary>
    public ReviewAgent(IMultiTurnAgent agent, ILogger<ReviewAgent> logger, Func<IDisposable>? suppressSpawning = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _suppressSpawning = suppressSpawning;
    }

    /// <summary>
    /// Dispatches the installed PR context gatherer as a dedicated first turn and accepts only its sourced
    /// schema-v1 manifest. The bootstrap is serialized as compact navigation data; it is not expanded into a
    /// diff or provider-body dump here.
    /// </summary>
    public async Task<DynamicContextGatheringResult> GatherContextAsync(
        DynamicContextBootstrap bootstrap,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        const string gathererTemplate = "code-reviewer:pr-context-gatherer";
        var input = $$"""
            Dispatch the installed `{{gathererTemplate}}` agent now.
            This is a context-gathering turn, not a code-review or publication turn.
            Follow the supplied references through PR-scoped read-only tools. Perform at least two scoped reads.
            Return only one JSON object matching context-manifest schema version 1. Every material claim must cite
            one or more exact, non-trivial provider, commit, file, or discussion references that appear verbatim in
            successful scoped read results. The host will validate those references and attach immutable audit source
            record ids and accepted hashes; do not invent those host-owned values. Mark any missing repo, head, or
            workspace scope as a required gap instead of guessing.

            Bootstrap JSON:
            {{JsonSerializer.Serialize(bootstrap)}}
            """;

        var result = await RunTurnAsync(input, deadlineUtc, cancellationToken).ConfigureAwait(false);
        DynamicContextManifestDraft manifest;
        try
        {
            manifest =
                JsonSerializer.Deserialize<DynamicContextManifestDraft>(
                    result.ReviewText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? throw new InvalidOperationException("The context gatherer returned an empty manifest.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The context gatherer returned malformed manifest JSON.", ex);
        }

        if (manifest.Claims is null || manifest.Gaps is null)
        {
            throw new InvalidOperationException("The context gatherer returned malformed manifest JSON.");
        }

        ValidateManifest(manifest, bootstrap);
        _logger.LogInformation(
            "Context gathering turn {RunId} produced semantic schema {SchemaVersion} with {ClaimCount} claim(s) on thread {ThreadId}; host evidence validation is still required.",
            result.RunId,
            manifest.Version,
            manifest.Claims.Count,
            result.ThreadId
        );
        return new DynamicContextGatheringResult(manifest, result.RunId, result.ThreadId);
    }

    /// <summary>
    /// Sends <paramref name="input"/> as one user turn and collects the assistant's provisional review text.
    /// Tolerates a blank answer: this turn is never the authoritative review, so an empty provisional is a
    /// fact about a parent that deferred everything to its children, not a failure.
    /// </summary>
    public async Task<ReviewAgentResult> CollectProvisionalAsync(
        string input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var result = await RunTurnAsync(input, deadlineUtc, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Provisional review turn {RunId} produced {Length} chars on thread {ThreadId}; children may still be running.",
            result.RunId,
            result.ReviewText.Length,
            result.ThreadId
        );
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
        CancellationToken cancellationToken
    )
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
                    + (
                        allowInlinePosting
                            ? "nothing authoritative to persist and the agent was expected to post it inline."
                            : "nothing authoritative to persist or hand to the publisher."
                    )
            );
        }

        _logger.LogInformation(
            "Synthesis turn {RunId} produced the authoritative review ({Length} chars, inline posting {InlinePosting}).",
            result.RunId,
            result.ReviewText.Length,
            allowInlinePosting
        );
        return result;
    }

    private static void ValidateManifest(DynamicContextManifestDraft manifest, DynamicContextBootstrap bootstrap)
    {
        if (manifest.Version != DynamicContextManifest.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"The context manifest schema version {manifest.Version} is unsupported; expected {DynamicContextManifest.SchemaVersion}."
            );
        }

        if (manifest.EngagementRoundId != bootstrap.EngagementRoundId)
        {
            throw new InvalidOperationException("The context manifest names a different engagement round.");
        }

        if (
            manifest.Claims.Any(claim =>
                string.IsNullOrWhiteSpace(claim.ClaimId)
                || string.IsNullOrWhiteSpace(claim.Text)
                || claim.Citations.Count == 0
                || claim.Citations.Any(string.IsNullOrWhiteSpace)
            )
        )
        {
            throw new InvalidOperationException("Every material context claim must carry a bounded citation.");
        }

        if (manifest.Gaps.Any(gap => string.IsNullOrWhiteSpace(gap.Scope)))
        {
            throw new InvalidOperationException("Every context gap must identify its scope.");
        }
    }

    /// <summary>
    /// Runs one turn under the shared budget: refuses to start a turn the budget can no longer cover, binds
    /// a deadline-bounded loop to it, then collects the finalized assistant prose. The thread id is read
    /// AFTER the run because the S2S agent provisions lazily (its <c>ThreadId</c> is empty until then).
    /// </summary>
    private async Task<ReviewAgentResult> RunTurnAsync(
        string input,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    )
    {
        if (DateTimeOffset.UtcNow >= deadlineUtc)
        {
            throw new TimeoutException(
                $"The review budget expired at {deadlineUtc:O}; refusing to start a turn it cannot finish."
            );
        }

        (_agent as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

        var collected = await AgentTextCollector.CollectAsync(_agent, input, cancellationToken).ConfigureAwait(false);

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
