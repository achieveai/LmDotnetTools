using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The <see cref="IMultiTurnAgent"/> adapter that drives one review turn against a running
/// <b>LmStreaming.Sample</b> review host over the S2S REST API (via <see cref="LmStreamingS2SClient"/>),
/// so the review is a real LmStreaming-hosted conversation (parent loop + <c>code-reviewer:*</c> sub-agent
/// tree) rather than an in-process loop. It satisfies exactly the slice of the interface that
/// <see cref="AgentTextCollector"/> drives — one <see cref="ExecuteRunAsync"/> — and stays thin:
/// <list type="number">
/// <item>lazily <b>provision</b> a conversation once (caching the server-minted <c>thread-{Guid:N}</c>),
///   handing the review profile's system prompt to the host as the conversation's system prompt appendix —
///   without it the hosted run sees only the diff under a generic workspace-agent prompt and never follows
///   the daemon's methodology, sub-agent dispatch or output contract,</item>
/// <item><b>send</b> the review input as one user message (getting an <c>inputId</c> to poll by),</item>
/// <item><b>poll</b> <see cref="LmStreamingS2SClient.GetStatusByInputIdAsync"/> to a terminal status with a
///   bounded backoff and an overall timeout, and</item>
/// <item>yield the review text from <c>status.Response</c> as a <b>single finalized</b>
///   <see cref="TextMessage"/> (<see cref="Role.Assistant"/>, non-thinking) — the final assistant text is
///   already resolved server-side, so no delta reassembly is needed and one yield feeds the collector.</item>
/// </list>
/// <para>
/// <see cref="ThreadId"/> surfaces the server-minted conversation id (the deep-link target the executor
/// appends to the posted PR comment); <see cref="CurrentRunId"/> comes from the polled status. The
/// background-loop members (<see cref="SendAsync"/>/<see cref="TrySendAsync"/>, <see cref="SubscribeAsync"/>,
/// <see cref="RunAsync"/>, <see cref="StopAsync"/>, <see cref="IsRunning"/>) are implemented minimally: the
/// daemon only ever drives this agent collect-only through <see cref="ExecuteRunAsync"/>.
/// </para>
/// <para>
/// It declares <see cref="IReviewLoopSubAgentSurface"/> so the synthesis turn's no-new-children guarantee is
/// REAL over S2S too: <see cref="SuppressSpawning"/> opens a scope that makes the next
/// <see cref="ExecuteRunAsync"/> ask the host to run that turn with sub-agent spawning suppressed, and the
/// host refuses the send outright if it cannot honour it. Its <see cref="CompletionSource"/> is null on
/// purpose — the children live in the HOST's process, so the barrier reads them through the injected
/// out-of-process source over the recursive sub-agent endpoint.
/// </para>
/// </summary>
internal sealed class S2SReviewAgent
    : IMultiTurnAgent, IDeadlineBoundedReviewLoop, IResumableReviewTurn, IReviewLoopSubAgentSurface
{
    /// <summary>Terminal run statuses that end the poll loop (matches <c>ConversationRunStatus</c> names).</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Completed",
        "Errored",
        "Interrupted",
    };

    private readonly LmStreamingS2SClient _client;
    private readonly string _workspaceId;
    private readonly string _providerId;
    private readonly string _modeId;
    private readonly string? _systemPrompt;
    private readonly string? _title;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollMaxInterval;
    private readonly TimeSpan _terminalConfirmDelay;
    private readonly TimeSpan _interruptedGrace;
    private readonly TimeSpan _overallTimeout;
    private readonly ILogger<S2SReviewAgent> _logger;
    private readonly Action<string>? _onConversationMinted;

    private string? _threadId;
    private string? _currentRunId;
    private DateTimeOffset? _deadlineUtc;
    private int _spawnSuppressionDepth;
    private string? _armedIdempotencyKey;
    private string? _armedAcceptedInputId;
    private Action<string>? _onInputAccepted;
    private Action<string>? _onRunConversationMinted;

    /// <summary>
    /// Builds the adapter. <paramref name="existingThreadId"/> seeds an ALREADY-PROVISIONED hosted
    /// conversation: supplied, the agent rejoins that thread and never calls <c>ProvisionAsync</c>. That is
    /// how a review resumed after a daemon restart continues on the conversation its provisional turn ran on
    /// — minting a second one would lose the review history the synthesis turn depends on and orphan the
    /// deep-link already posted on the PR.
    /// </summary>
    public S2SReviewAgent(
        LmStreamingS2SClient client,
        string workspaceId,
        string providerId,
        string modeId,
        string? systemPrompt,
        string? title,
        ILogger<S2SReviewAgent> logger,
        TimeSpan? pollInterval = null,
        TimeSpan? pollMaxInterval = null,
        TimeSpan? overallTimeout = null,
        TimeSpan? terminalConfirmDelay = null,
        TimeSpan? interruptedGrace = null,
        Action<string>? onConversationMinted = null,
        string? existingThreadId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _workspaceId = workspaceId;
        _providerId = providerId;
        _modeId = modeId;
        _systemPrompt = systemPrompt;
        _title = title;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onConversationMinted = onConversationMinted;
        _threadId = string.IsNullOrWhiteSpace(existingThreadId) ? null : existingThreadId;

        // Live workspace-agent reviews are long (a run can reach turn 17 of 150), so the poll must be patient
        // but not busy: start tight, back off geometrically to a ceiling, and bound the whole wait.
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollMaxInterval = pollMaxInterval ?? TimeSpan.FromSeconds(15);
        _overallTimeout = overallTimeout ?? TimeSpan.FromMinutes(30);

        // Cadence and window for settling an ambiguous Interrupted reading (see SettleInterruptedAsync). The
        // grace is generous because waiting one out is free — an Interrupted status never carries review text,
        // so the only cost of patience is latency on a review that has genuinely died.
        _terminalConfirmDelay = terminalConfirmDelay ?? TimeSpan.FromSeconds(2);
        _interruptedGrace = interruptedGrace ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>The server-minted run id from the last polled status (null before the first run completes).</summary>
    public string? CurrentRunId => _currentRunId;

    /// <summary>
    /// The server-minted conversation id (<c>thread-{Guid:N}</c>) once provisioned. Before the first run it is
    /// an empty string (the agent has not yet talked to the review host); the collector reads it only after the
    /// run, by which point it is the real minted id the deep-link points at.
    /// </summary>
    public string ThreadId => _threadId ?? string.Empty;

    /// <summary>Always false — this adapter has no background loop; it is driven collect-only.</summary>
    public bool IsRunning => false;

    /// <inheritdoc />
    public void UseDeadline(DateTimeOffset deadlineUtc) => _deadlineUtc = deadlineUtc;

    /// <inheritdoc />
    public void ObserveConversationMint(Action<string> onConversationMinted)
    {
        ArgumentNullException.ThrowIfNull(onConversationMinted);
        _onRunConversationMinted = onConversationMinted;
    }

    /// <inheritdoc />
    public void ArmTurnCheckpoint(string idempotencyKey, string? acceptedInputId, Action<string>? onInputAccepted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        _armedIdempotencyKey = idempotencyKey;
        _armedAcceptedInputId = string.IsNullOrWhiteSpace(acceptedInputId) ? null : acceptedInputId;
        _onInputAccepted = onInputAccepted;
    }

    /// <summary>
    /// Always <c>null</c>: this agent's children run in the REVIEW HOST's process, so there is no in-process
    /// manager to read them from. The barrier uses the executor's injected out-of-process source instead.
    /// </summary>
    public IReviewSubAgentCompletionSource? CompletionSource => null;

    /// <inheritdoc />
    public Func<IDisposable>? SuppressSpawning => OpenSpawnSuppression;

    /// <summary>
    /// Opens a scope in which every <see cref="ExecuteRunAsync"/> asks the host to run its turn with NEW
    /// sub-agent spawning suppressed. Reference-counted so nesting is safe, and released on dispose so the
    /// thread is back to normal for any later turn.
    /// </summary>
    private IDisposable OpenSpawnSuppression()
    {
        _ = Interlocked.Increment(ref _spawnSuppressionDepth);
        return new SpawnSuppressionScope(this);
    }

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var input = ExtractUserText(userInput);
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("S2SReviewAgent.ExecuteRunAsync received no user text to send.");
        }

        // Either channel can demand it: the executor's synthesis scope (SuppressSpawning) or an input that
        // carries the SDK's per-turn flag. The host acknowledges or refuses — it is never a hint.
        var suppressSpawning =
            Volatile.Read(ref _spawnSuppressionDepth) > 0 || userInput.SuppressSubAgentSpawning;

        var threadId = await EnsureProvisionedAsync(ct).ConfigureAwait(false);

        // Consume the arming BEFORE the turn runs: it applies to this turn only, so a later turn on the same
        // agent can neither rejoin a spent input nor re-fire a checkpoint that has already been persisted.
        var rejoinInputId = _armedAcceptedInputId;
        var idempotencyKey = _armedIdempotencyKey;
        var onInputAccepted = _onInputAccepted;
        _armedAcceptedInputId = null;
        _armedIdempotencyKey = null;
        _onInputAccepted = null;

        string inputId;
        if (rejoinInputId is not null)
        {
            // The host already accepted this input and is (or was) producing an answer for it. Sending the turn
            // again would run the same review twice on one conversation and double-spend the shared budget, so
            // a checkpointed input is only ever polled. The host resolves it from its persisted ledger, which
            // is why this still works after BOTH processes have restarted.
            inputId = rejoinInputId;
            _logger.LogInformation(
                "Rejoining already-accepted S2S review input {InputId} on thread {ThreadId} instead of "
                    + "re-sending it; polling to terminal.",
                inputId,
                threadId);
        }
        else
        {
            inputId = await _client
                .SendMessageAsync(threadId, input, suppressSpawning, idempotencyKey, ct)
                .ConfigureAwait(false);
            // Reported before the first poll so the caller's checkpoint covers the whole wait, not just a wait
            // that happened to finish. When the send carried a key this is a confirmation rather than the only
            // record of it — the key is reconstructible, so losing this callback costs one extra send, not a
            // duplicate turn.
            onInputAccepted?.Invoke(inputId);
            _logger.LogInformation(
                "S2S review message queued on thread {ThreadId} (inputId {InputId}, idempotency key {Key}, "
                    + "spawning suppressed {SuppressSpawning}); polling to terminal.",
                threadId,
                inputId,
                idempotencyKey ?? "(none)",
                suppressSpawning);
        }

        var status = await PollToTerminalAsync(threadId, inputId, ct).ConfigureAwait(false);
        _currentRunId = status.RunId;

        if (!string.Equals(status.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"S2S review run {status.RunId} on thread {threadId} ended with terminal status "
                    + $"{status.Status}, not Completed.");
        }

        var reviewText = status.ResponseText;
        if (string.IsNullOrWhiteSpace(reviewText))
        {
            throw new InvalidOperationException(
                $"S2S review run {status.RunId} on thread {threadId} reached Completed with no review text.");
        }

        // ONE finalized assistant message. AgentTextCollector prefers a finalized TextMessage over streamed
        // deltas, so this single yield is the collected review text verbatim — no delta/GenerationId mirroring.
        yield return new TextMessage
        {
            Text = reviewText,
            Role = Role.Assistant,
            IsThinking = false,
            ThreadId = threadId,
            RunId = status.RunId,
        };
    }

    /// <summary>
    /// Provisions the conversation on first use and caches the minted thread id, so a second
    /// <see cref="ExecuteRunAsync"/> (the authoritative synthesis turn) reuses the same thread rather than
    /// opening a new one. A thread id seeded at construction (a resumed review) short-circuits here exactly
    /// the same way, so a resume never re-provisions. Best-effort sets a human-readable title once, right
    /// after provisioning.
    /// </summary>
    private async Task<string> EnsureProvisionedAsync(CancellationToken ct)
    {
        if (_threadId is not null)
        {
            // A seeded thread is a RESUME. Whether it is still the right conversation to continue on — same
            // workspace, same review lifecycle — is decided by whoever seeded it, before this agent exists;
            // there is nothing here that could re-derive it, and re-provisioning would silently discard the
            // in-flight sub-agent tree the caller is resuming onto.
            return _threadId;
        }

        var threadId = await _client
            .ProvisionAsync(_workspaceId, _providerId, _modeId, _systemPrompt, ct)
            .ConfigureAwait(false);
        _threadId = threadId;
        _logger.LogInformation(
            "Provisioned S2S review conversation {ThreadId} (workspace {WorkspaceId}, provider {ProviderId}, "
                + "mode {ModeId}, system prompt {SystemPromptChars} chars).",
            threadId,
            _workspaceId,
            _providerId,
            _modeId,
            _systemPrompt?.Length ?? 0);

        // The run-scoped checkpoint comes FIRST and is deliberately NOT guarded: from this line on there is a
        // hosted conversation that will fan out a sub-agent tree, and a caller that cannot record its identity
        // cannot find it again after a restart — it would mint a second conversation and fan out a second tree
        // on top of the first. Failing the mint leaves one unrecorded conversation; swallowing leaves two
        // running reviews, so this is the one hook here that is allowed to take the review down with it.
        _onRunConversationMinted?.Invoke(threadId);

        // Retention bookkeeping, by contrast, is best-effort and runs BEFORE the (also best-effort) title
        // update so a cosmetic failure can never cost the conversation its retention row. This is the single
        // choke point through which the review, judge and A/B arms all provision — and only the review's
        // thread id ever reaches an artifact, so recording anywhere downstream would leave the other arms
        // un-aged and permanent.
        if (_onConversationMinted is not null)
        {
            try
            {
                _onConversationMinted(threadId);
            }
            catch (Exception ex)
            {
                // The cost of losing this row is a conversation that outlives its retention window — strictly
                // better than a review that dies over a ledger write.
                _logger.LogWarning(
                    ex,
                    "Could not record S2S review conversation {ThreadId} in the deep-link retention ledger; "
                        + "it will not be discarded automatically.",
                    threadId);
            }
        }

        if (!string.IsNullOrWhiteSpace(_title))
        {
            try
            {
                await _client.UpdateMetadataAsync(threadId, _title, preview: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cosmetic title is not worth failing the review over.
                _logger.LogWarning(ex, "Could not set title on S2S review conversation {ThreadId}.", threadId);
            }
        }

        return threadId;
    }

    /// <summary>
    /// Polls the run status by input id until a terminal status, with geometric backoff capped at
    /// <see cref="_pollMaxInterval"/> and an overall <see cref="_overallTimeout"/>. Throws
    /// <see cref="TimeoutException"/> if the run does not finish in time.
    /// <para>
    /// <b><c>Interrupted</c> is not taken at face value.</b> The host records an input as accepted before the
    /// agent loop drains it, and a run-ledger reconciliation that lands in that gap synthesizes an
    /// <c>Interrupted</c> row for the not-yet-drained input; the loop then drains it and starts the REAL run
    /// under a different run id, which status-by-inputId resolves to (newest row wins). Observed live: the
    /// first poll after send read <c>Interrupted</c> for a run that never produced a message, while the real
    /// run completed with the review text moments later. Waiting an <c>Interrupted</c> reading out is free —
    /// the host never attaches response text to it — so we re-poll through <see cref="_interruptedGrace"/>
    /// before believing it. <c>Completed</c>/<c>Errored</c> carry resolved text and are returned immediately.
    /// </para>
    /// <para>
    /// The wait is bounded by <see cref="_overallTimeout"/> <b>clipped to the caller's absolute deadline</b>
    /// (see <see cref="UseDeadline"/>) — collect → barrier → synthesize share ONE budget, so a later turn
    /// must not open a fresh full-length window with most of that budget already spent.
    /// </para>
    /// </summary>
    private async Task<S2SStatusResult> PollToTerminalAsync(string threadId, string inputId, CancellationToken ct)
    {
        var deadline = ClampToBudget(DateTimeOffset.UtcNow + _overallTimeout);
        var interval = _pollInterval;
        var status = new S2SStatusResult("NotStarted", null, null);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"S2S review run on thread {threadId} did not reach a terminal status before {deadline:O} "
                    + $"(last status: {status.Status})."
                );
            }

            status = await _client.GetStatusByInputIdAsync(threadId, inputId, ct).ConfigureAwait(false);

            if (IsInterrupted(status))
            {
                var settled = await SettleInterruptedAsync(threadId, inputId, status, ct).ConfigureAwait(false);
                if (settled is not null)
                {
                    return settled;
                }

                // Superseded: the input is bound to a different run now. Re-poll it at once, tightly.
                interval = _pollInterval;
                continue;
            }

            if (TerminalStatuses.Contains(status.Status))
            {
                return status;
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);

            // Geometric backoff to the ceiling — patient with long hosted runs without hammering the host.
            var next = interval + interval;
            interval = next > _pollMaxInterval ? _pollMaxInterval : next;
        }
    }

    private static bool IsInterrupted(S2SStatusResult status) =>
        string.Equals(status.Status, "Interrupted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clips a per-turn window to the caller's shared absolute budget, when one was supplied. Without this
    /// every turn would restart its own <see cref="_overallTimeout"/>, so a two-turn review could consume the
    /// budget twice over.
    /// </summary>
    private DateTimeOffset ClampToBudget(DateTimeOffset windowEnd) =>
        _deadlineUtc is { } budget && budget < windowEnd ? budget : windowEnd;

    /// <summary>
    /// Decides whether an <c>Interrupted</c> reading is the input's real outcome. Re-polls every
    /// <see cref="_terminalConfirmDelay"/> for up to <see cref="_interruptedGrace"/>: returns the reading (the
    /// run really is dead — a lost input whose run will never restart) if it stays <c>Interrupted</c> on the
    /// SAME run id for the whole window, or <c>null</c> as soon as the input moves to a different run, meaning
    /// the caller should keep polling that run.
    /// </summary>
    private async Task<S2SStatusResult?> SettleInterruptedAsync(
        string threadId,
        string inputId,
        S2SStatusResult interrupted,
        CancellationToken ct
    )
    {
        var graceEnd = ClampToBudget(DateTimeOffset.UtcNow + _interruptedGrace);

        while (DateTimeOffset.UtcNow < graceEnd)
        {
            await Task.Delay(_terminalConfirmDelay, ct).ConfigureAwait(false);

            var next = await _client.GetStatusByInputIdAsync(threadId, inputId, ct).ConfigureAwait(false);
            if (IsInterrupted(next) && string.Equals(next.RunId, interrupted.RunId, StringComparison.Ordinal))
            {
                continue;
            }

            _logger.LogInformation(
                "S2S review on thread {ThreadId} read a superseded Interrupted run {RunId}; the host re-bound the "
                    + "input to run {NextRunId} (status {NextStatus}). Continuing to poll.",
                threadId,
                interrupted.RunId,
                next.RunId,
                next.Status
            );
            return null;
        }

        _logger.LogWarning(
            "S2S review on thread {ThreadId} stayed Interrupted on run {RunId} for {GraceSeconds:0}s; treating it "
                + "as the input's final state.",
            threadId,
            interrupted.RunId,
            _interruptedGrace.TotalSeconds
        );
        return interrupted;
    }

    /// <summary>Concatenates the user turn's non-empty text messages (the collector sends exactly one).</summary>
    private static string ExtractUserText(UserInput userInput)
    {
        var parts = userInput.Messages
            .OfType<TextMessage>()
            .Where(static m => m.Role == Role.User && !string.IsNullOrEmpty(m.Text))
            .Select(static m => m.Text);
        return string.Join("\n", parts);
    }

    // ── Background-loop members: not used on the collect-only review drive ──────────────────────────

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "S2SReviewAgent is driven collect-only via ExecuteRunAsync; background SendAsync is not supported.");

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "S2SReviewAgent is driven collect-only via ExecuteRunAsync; background TrySendAsync is not supported.");

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // No background loop → no out-of-band message stream. Yield nothing (a valid empty async sequence).
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Decrements the suppression count once, however many times it is disposed.</summary>
    private sealed class SpawnSuppressionScope(S2SReviewAgent agent) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _ = Interlocked.Decrement(ref agent._spawnSuppressionDepth);
            }
        }
    }
}
