using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Builds the live <see cref="IMultiTurnAgent"/> loop a daemon agent (Review / Judge / Knowledge / B
/// variant) drives for one collect-only run. This is the single seam between the declarative
/// <see cref="AgentProfile"/> (built by the workspace invocation adapter) and the concrete provider
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
        string? resumeHostedThreadId = null
    );

    /// <summary>
    /// What <see cref="Create"/> will ACTUALLY run on given <paramref name="requestedModelId"/> — which is
    /// not always what was asked for. An implementation may substitute its own selection, or name an
    /// identity in a form the caller did not supply (the S2S factory answers a <c>lmstreaming:</c>-prefixed
    /// id), so a caller that wants to record or reason about the model has to ask instead of assume.
    /// <para>
    /// Returns <c>null</c> when the transport exposes no model identity at all. Null is <b>unknown</b>, not
    /// a value: a caller comparing two nulls must not conclude the two runs shared a model, and one
    /// persisting a null must not present it as a measurement. Anything a caller records from this is a
    /// claim about production, so a factory must never return an id it will not honour.
    /// </para>
    /// </summary>
    string? ResolveEffectiveModelId(string? requestedModelId);

    /// <summary>
    /// Whether <see cref="Create"/> actually RUNS the <c>modelId</c> it is handed. <c>false</c> means the
    /// argument is discarded and the transport selects the model itself, so a per-call id is a request the
    /// wire never carries.
    /// <para>
    /// This exists because the daemon PERSISTS the model it asked for — the escalation ladder retries on
    /// <c>OverflowEscalationModelId</c> and the stage executor writes that id into the
    /// <c>review-provisional</c> checkpoint's <c>ReviewLifecycleIdentity</c>, from which
    /// <c>DaemonCorpusReader</c> credits the whole eval candidate. Recorded against a factory that answers
    /// <c>false</c>, that id names a model nothing ran, and it is silent: at the corpus layer a wrong model id
    /// is indistinguishable from a right one. So the caller stamps the override only where it will be honoured
    /// and keeps <c>review_run.model_id</c> otherwise.
    /// </para>
    /// <para>
    /// Deliberately NOT derivable from <see cref="ResolveEffectiveModelId"/>. That answers a different
    /// question — what identity the transport can NAME — and the two come apart in both directions: a
    /// factory can honour nothing yet still name a selector (which the S2S factory did before it forwarded
    /// the id, and which any transport that picks its own model still does), and a factory could honour the
    /// request while naming nothing at all (<c>null</c> is unknown, not "no model"). Comparing the resolved
    /// id against the requested one would read that second factory as discarding, and quietly drop the
    /// escalation attribution this flag exists to protect. Note the S2S factory answers a PREFIXED id, so
    /// even where it honours the request the two strings are never equal.
    /// </para>
    /// <para>
    /// A property, not a per-call predicate: an implementation either wires the argument through or does not,
    /// and asking per call would invite an answer that varies where no transport's does.
    /// </para>
    /// </summary>
    bool HonoursRequestedModelId { get; }
}

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
internal interface IResumableReviewTurn
{
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
