using System.Globalization;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>What one <see cref="ConversationTranscriptWriter.FlushAsync"/> attempt achieved.</summary>
/// <remarks>
///     The mirror is best-effort, so this is advisory rather than a status code: its only real consumer
///     decision is "should I schedule another flush?", and only <see cref="Deferred"/> answers yes.
///     <see cref="Unavailable"/> deliberately does NOT ask for a re-schedule — a conversation with no
///     sandbox session would otherwise spin the drain loop forever.
/// </remarks>
public enum TranscriptFlushOutcome
{
    /// <summary>Every file already ended at the current watermark; nothing was appended.</summary>
    UpToDate,

    /// <summary>At least one transcript file was appended to and its watermark advanced.</summary>
    Written,

    /// <summary>
    ///     Work remains: a read never settled, a gateway call failed, or the sub-agent fan-out was capped.
    ///     The watermarks involved are unadvanced, so a later flush repeats the work. <b>Re-schedule.</b>
    /// </summary>
    Deferred,

    /// <summary>
    ///     There is no workspace session to write into (no workspace bound, no session, credential
    ///     conflict, gateway unreachable). Nothing was written and nothing is pending.
    /// </summary>
    Unavailable,
}

/// <summary>
///     Mirrors ONE conversation's persisted messages into <c>.conversations/</c> inside that
///     conversation's sandbox workspace, as append-only JSONL (#251) — the main thread into
///     <c>{slug(title)}-{shortThreadId}.jsonl</c>, each sub-agent into a file under the sibling
///     <c>{slug(title)}-{shortThreadId}_agents/</c> directory. One instance per conversation; it owns
///     that conversation's watermarks and its last-known file leaf.
/// </summary>
/// <remarks>
///     <para>
///     <b>Failure policy: best-effort, never propagating.</b> Every path catches, logs, and leaves the
///     watermark unadvanced. The failure direction is always duplicate-on-retry, never silent loss — a
///     reader deduplicates on <c>uid</c> (which is a pure function of the persisted row id), so a repeated
///     append costs bytes, whereas a skipped append costs the record itself.
///     </para>
///     <para>
///     <b>Writes go through exactly one shell call site</b> (<see cref="AppendScript"/>).
///     <c>ExecuteWorkspaceCommandAsync</c> is a native argv vector with no implicit shell, so a shell is
///     invoked explicitly. The script text is a compile-time constant and <i>no dynamic value is ever
///     interpolated into it</i>: the destination directory, the temp file and the destination file arrive
///     as positional parameters <c>$1</c>/<c>$2</c>/<c>$3</c>. That matters because the file leaf is
///     derived from a user-authored conversation title. <c>--</c> on the pure-argv calls stops option
///     parsing, which is worth having, but it is the positional parameters — not <c>--</c> — that make a
///     title containing <c>$(…)</c> or whitespace inert.
///     </para>
///     <para>
///     <b>Concurrency:</b> <see cref="FlushAsync"/> is not re-entrant and must not be called concurrently
///     with itself for the same conversation. <see cref="TranscriptFlushScheduler"/> guarantees that (a
///     single drain loop). The sub-agent fan-out inside one flush is likewise strictly sequential; see
///     <see cref="FlushSubAgentsAsync"/>.
///     </para>
/// </remarks>
public sealed class ConversationTranscriptWriter
{
    /// <summary>Workspace-relative directory every transcript file lives under.</summary>
    public const string TranscriptDirectory = ".conversations";

    /// <summary>Extension of every transcript file.</summary>
    public const string TranscriptExtension = ".jsonl";

    /// <summary>Suffix appended to the main file leaf to form the sibling sub-agent directory.</summary>
    public const string AgentsDirectorySuffix = "_agents";

    /// <summary>Extension of the staged temp file the splice consumes.</summary>
    public const string TempExtension = ".part";

    /// <summary>Directory the staged temp file is PUT into, workspace-relative.</summary>
    public const string TempDirectory = TranscriptDirectory + "/.tmp";

    /// <summary>Name of the containment file written once per conversation.</summary>
    public const string GitignoreName = "gitignore";

    /// <summary>
    ///     How many trailing lines the cold-start watermark probe asks <c>tail</c> for. Five, not one:
    ///     a previous run can have been killed mid-splice, leaving a torn final line that will not parse.
    ///     Reading a window and walking it bottom-up finds the last INTACT record instead of concluding
    ///     "unreadable" and re-appending the whole history.
    /// </summary>
    public const int WatermarkTailLines = 5;

    /// <summary>
    ///     How many times a re-read may disagree with its predecessor before the flush gives up. See
    ///     <see cref="ReadStableAsync"/> for why reading twice is not optional.
    /// </summary>
    public const int MaxStabilityAttempts = 3;

    /// <summary>Default cap on sub-agent files written per flush. See <see cref="FlushSubAgentsAsync"/>.</summary>
    public const int DefaultMaxSubAgentFilesPerFlush = 8;

    /// <summary>
    ///     Default pause between the two reads of the stability probe. Long enough for the fire-and-forget
    ///     persistence task of a just-completed turn to land, short enough to be invisible on a background
    ///     drain. Overridable (tests pass <see cref="TimeSpan.Zero"/>).
    /// </summary>
    public static readonly TimeSpan DefaultReadSettleDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    ///     Key in <see cref="ThreadMetadata.Properties"/> holding the conversation title. Deliberately the
    ///     UN-namespaced key <c>ConversationsController</c> writes on <c>PUT {threadId}/metadata</c>; a
    ///     retitle never reaches the message stream, so this metadata read is the mirror's only way to
    ///     notice one.
    /// </summary>
    private const string TitlePropertyKey = "title";

    /// <summary>
    ///     THE one shell call site. Built from concatenated literals rather than a raw string literal so
    ///     the line endings are LF regardless of how this source file is checked out — a script carrying
    ///     CR bytes fails inside the container in ways that are tedious to diagnose.
    /// </summary>
    /// <remarks>
    ///     <para><c>$1</c> destination directory, <c>$2</c> staged temp file, <c>$3</c> destination file.</para>
    ///     <para>
    ///     The middle line is the <b>newline weld</b>. If a previous splice was interrupted the destination
    ///     can end mid-record with no trailing newline; appending straight onto it would fuse two records
    ///     into one unparseable line, destroying the intact record as well as the torn one. Welding a
    ///     newline on first costs one blank-ish boundary and keeps the damage to the single torn line.
    ///     </para>
    ///     <para>
    ///     <c>rm -f "$2"</c> is chained after a successful append, so a failure leaks at most one
    ///     <c>.part</c> file per conversation — a name no <c>**/*.jsonl</c> scan matches, in a directory a
    ///     <c>.gitignore</c> already covers.
    ///     </para>
    /// </remarks>
    private const string AppendScript =
        "mkdir -p \"$1\" || exit 1\n"
        + "if [ -s \"$3\" ] && [ -n \"$(tail -c1 \"$3\")\" ]; then printf '\\n' >> \"$3\" || exit 1; fi\n"
        + "cat \"$2\" >> \"$3\" && rm -f \"$2\"\n";

    private readonly IConversationStore _store;
    private readonly IWorkspaceFileBrowser _fileBrowser;
    private readonly ConversationDescendantScanner _descendants;
    private readonly ILogger<ConversationTranscriptWriter> _logger;
    private readonly TimeSpan _readSettleDelay;
    private readonly int _maxSubAgentFilesPerFlush;
    private readonly string _shortThreadId;
    private readonly string _tempPath;

    /// <summary>
    ///     Last appended <c>uid</c> per file, keyed by the THREAD whose rows that file holds — the
    ///     conversation for the main file, the sub-agent's own thread for an <c>_agents/</c> file. Keyed by
    ///     thread and not by path on purpose: a retitle renames the file, and a watermark that moved with
    ///     the name would restart the whole history every time somebody edits a title.
    /// </summary>
    private readonly Dictionary<string, string> _watermarks = new(StringComparer.Ordinal);

    /// <summary>
    ///     The <c>parent_uid</c> each sub-agent file's first line was minted with, so it stays stable for
    ///     the life of the process instead of drifting with the main file's watermark.
    /// </summary>
    private readonly Dictionary<string, string?> _agentRootParentUids = new(StringComparer.Ordinal);

    /// <summary>Files already warned about, so the "appending everything" warning stays a one-off.</summary>
    private readonly HashSet<string> _fullAppendWarned = new(StringComparer.Ordinal);

    private string? _leaf;
    private bool _gitignoreWritten;
    private bool _agentsDirectoryTouched;
    private int _subAgentCursor;
    private int _sawSubAgentActivity;

    /// <summary>Creates the writer for one conversation.</summary>
    /// <param name="threadId">The conversation's thread id.</param>
    /// <param name="store">Source of the persisted rows and of the conversation's metadata.</param>
    /// <param name="fileBrowser">The sandbox seam: session resolution, file PUT, command execution.</param>
    /// <param name="descendants">Cached sub-agent graph used for the fan-out.</param>
    /// <param name="logger">Every failure reports here; nothing is thrown to the caller.</param>
    /// <param name="readSettleDelay">Overrides <see cref="DefaultReadSettleDelay"/>.</param>
    /// <param name="maxSubAgentFilesPerFlush">Overrides <see cref="DefaultMaxSubAgentFilesPerFlush"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="threadId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSubAgentFilesPerFlush"/> is not positive.</exception>
    public ConversationTranscriptWriter(
        string threadId,
        IConversationStore store,
        IWorkspaceFileBrowser fileBrowser,
        ConversationDescendantScanner descendants,
        ILogger<ConversationTranscriptWriter> logger,
        TimeSpan? readSettleDelay = null,
        int maxSubAgentFilesPerFlush = DefaultMaxSubAgentFilesPerFlush)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileBrowser);
        ArgumentNullException.ThrowIfNull(descendants);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubAgentFilesPerFlush);

        ThreadId = threadId;
        _store = store;
        _fileBrowser = fileBrowser;
        _descendants = descendants;
        _logger = logger;
        _readSettleDelay = readSettleDelay ?? DefaultReadSettleDelay;
        _maxSubAgentFilesPerFlush = maxSubAgentFilesPerFlush;
        _shortThreadId = WorkspaceTranscriptLine.ShortId(threadId);
        _tempPath = $"{TempDirectory}/{_shortThreadId}{TempExtension}";
    }

    /// <summary>The conversation this writer mirrors.</summary>
    public string ThreadId { get; }

    /// <summary>
    ///     Records that sub-agent activity was observed (a relayed sub-agent result or notification), so
    ///     the next flush refreshes the descendant graph before fanning out.
    /// </summary>
    /// <remarks>
    ///     Separate from scheduling on purpose. A sub-agent finishing does not publish the conversation's
    ///     <c>RunCompletedMessage</c>, so a mirror that only listened for run completion would never write
    ///     an <c>_agents/</c> file for a sub-agent whose parent turn had already ended. It is also the only
    ///     safe trigger for <see cref="ConversationDescendantScanner.NoteAgentActivity"/>, which costs a
    ///     full store scan and must not be called once per flush. Cheap and thread-safe: callable straight
    ///     from the message-subscriber hot path.
    /// </remarks>
    public void NoteSubAgentActivity() => Interlocked.Exchange(ref _sawSubAgentActivity, 1);

    /// <summary>
    ///     Mirrors everything this conversation has persisted since the last successful flush. Never
    ///     throws: a failure is logged, the affected watermark is left unadvanced, and the outcome says
    ///     whether the caller should schedule another attempt.
    /// </summary>
    public async Task<TranscriptFlushOutcome> FlushAsync(CancellationToken ct = default)
    {
        try
        {
            return await FlushCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown, not a fault. Swallowed rather than rethrown because the mirror's contract is
            // that it never propagates; the interrupted work is picked up by the next completed run.
            _logger.LogDebug("Transcript flush for thread {ThreadId} cancelled", ThreadId);
            return TranscriptFlushOutcome.Unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcript flush for thread {ThreadId} failed", ThreadId);
            return TranscriptFlushOutcome.Unavailable;
        }
    }

    private async Task<TranscriptFlushOutcome> FlushCoreAsync(CancellationToken ct)
    {
        // Metadata carries BOTH values the flush needs — the workspace binding for step 1 and the title
        // for the retitle check — so it is read once here rather than twice at the two points of use.
        var metadata = await _store.LoadMetadataAsync(ThreadId, ct).ConfigureAwait(false);

        var sessionId = await ResolveSessionAsync(metadata, ct).ConfigureAwait(false);
        if (sessionId is null)
        {
            return TranscriptFlushOutcome.Unavailable;
        }

        var messages = await ReadStableAsync(ThreadId, ct).ConfigureAwait(false);
        if (messages is null)
        {
            return TranscriptFlushOutcome.Deferred;
        }

        // Step 4, and it must stay ahead of every append in this drain: a retitle that is noticed only
        // after the append leaves a second file holding a duplicate of the entire history.
        await ApplyRetitleAsync(sessionId, metadata, ct).ConfigureAwait(false);
        var leaf = _leaf!;

        var lines = WorkspaceTranscriptLine.ChainMessages(messages);
        var mainResult = await AppendAsync(
            sessionId,
            TranscriptDirectory,
            $"{TranscriptDirectory}/{leaf}{TranscriptExtension}",
            ThreadId,
            lines,
            ct
        ).ConfigureAwait(false);

        var wrote = mainResult == AppendResult.Appended;
        var deferred = mainResult == AppendResult.Failed;

        var (agentsWrote, agentsDeferred) = await FlushSubAgentsAsync(sessionId, leaf, ct).ConfigureAwait(false);
        wrote = wrote || agentsWrote;

        if (wrote)
        {
            await EnsureGitignoreAsync(sessionId, ct).ConfigureAwait(false);
        }

        return (deferred || agentsDeferred) ? TranscriptFlushOutcome.Deferred
            : wrote ? TranscriptFlushOutcome.Written
            : TranscriptFlushOutcome.UpToDate;
    }

    // -------- Step 1: session --------

    /// <summary>
    ///     Resolves the conversation's workspace session, or reports null for every reason a background
    ///     flush legitimately has none.
    /// </summary>
    /// <remarks>
    ///     The credential is always null: a flush has no inbound HTTP request to borrow one from. That is
    ///     also why an S2S-owned session reports <c>CredentialConflict</c> forever and gets no transcript
    ///     in v1 — an accepted, recorded gap (ADR 0011), not a bug to retry around.
    /// </remarks>
    private async Task<string?> ResolveSessionAsync(ThreadMetadata? metadata, CancellationToken ct)
    {
        var workspaceId = ReadWorkspaceId(metadata);
        if (workspaceId is null)
        {
            _logger.LogDebug("No workspace bound to thread {ThreadId}; transcript flush skipped", ThreadId);
            return null;
        }

        SandboxSessionResolution resolution;
        try
        {
            resolution = await _fileBrowser
                .ResolveThreadWorkspaceSessionAsync(ThreadId, workspaceId, requestCredential: null, ct)
                .ConfigureAwait(false);
        }
        catch (SandboxSessionUnavailableException ex)
        {
            // The gateway could not recreate an idle-evicted session. Expected background weather, not an
            // error the operator needs to see once per turn.
            _logger.LogDebug(ex, "Sandbox session unavailable for thread {ThreadId}; transcript flush skipped", ThreadId);
            return null;
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(ex, "Sandbox rejected session resolution for thread {ThreadId}", ThreadId);
            return null;
        }

        if (resolution is { Outcome: SandboxSessionResolutionOutcome.Resolved, Session: { } session })
        {
            return session.SessionId;
        }

        if (resolution.Outcome == SandboxSessionResolutionOutcome.CredentialConflict)
        {
            _logger.LogWarning(
                "Transcript flush for thread {ThreadId} skipped: session is owned by {ExistingAppId}",
                ThreadId,
                resolution.ExistingAppId ?? "(none)"
            );
            return null;
        }

        _logger.LogDebug("No sandbox session for thread {ThreadId}; transcript flush skipped", ThreadId);
        return null;
    }

    // -------- Step 2: read until stable --------

    /// <summary>
    ///     Reads a thread's rows twice and only accepts them once two consecutive reads agree, then
    ///     normalizes (step 3). Reports null when the rows never settled, which leaves every watermark
    ///     unadvanced and asks the caller to re-schedule.
    /// </summary>
    /// <remarks>
    ///     <b>Not defensive padding.</b> <c>MultiTurnAgentBase.AddToHistory</c> persists on a discarded,
    ///     unawaited task, and <c>CompleteRunAsync</c> publishes run completion without joining those
    ///     tasks — so at the instant a flush is triggered the turn's final assistant and reasoning rows are
    ///     commonly still in flight. Appending the truncated list would be bad enough on its own; the worse
    ///     half is that the late row then lands at the file tail on the NEXT flush, giving it a
    ///     <c>parent_uid</c> pointing at a row it did not follow. Comparing row ids is comparing uids: a
    ///     uid is a pure function of the persisted id.
    /// </remarks>
    private async Task<List<PersistedMessage>?> ReadStableAsync(string threadId, CancellationToken ct)
    {
        var previous = await _store.LoadMessagesAsync(threadId, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaxStabilityAttempts; attempt++)
        {
            if (_readSettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_readSettleDelay, ct).ConfigureAwait(false);
            }

            var current = await _store.LoadMessagesAsync(threadId, ct).ConfigureAwait(false);
            if (SameSequence(previous, current))
            {
                return TranscriptProjection.Normalize(current, excludeReasoning: false);
            }

            previous = current;
        }

        _logger.LogDebug(
            "Thread {ThreadId} rows did not settle in {Attempts} attempts; transcript flush deferred",
            threadId,
            MaxStabilityAttempts
        );
        return null;
    }

    private static bool SameSequence(IReadOnlyList<PersistedMessage> left, IReadOnlyList<PersistedMessage> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Id, right[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // -------- Step 4: retitle --------

    /// <summary>
    ///     Notices a title change and renames the conversation's file (and, when this writer has written
    ///     any, its sub-agent directory) before anything is appended this flush.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Adoption of the new leaf is gated on the FILE move succeeding: on failure the flush keeps
    ///     writing to the old path — which a failed <c>mv</c> left in place, complete and readable — and
    ///     retries next time.
    ///     </para>
    ///     <para>
    ///     The directory move is attempted only after the file move succeeded and only when this writer has
    ///     actually written a sub-agent file. A conversation with no sub-agents has no directory to move,
    ///     and issuing the <c>mv</c> anyway would fail every time and wedge the rename. If the directory
    ///     move does fail, the in-memory sub-agent watermarks describe files that did not move, so they are
    ///     dropped: the directory is rebuilt in full under the new name (duplicate rows, which dedupe on
    ///     <c>uid</c>) rather than silently resuming past rows the new files do not contain.
    ///     </para>
    /// </remarks>
    private async Task ApplyRetitleAsync(string sessionId, ThreadMetadata? metadata, CancellationToken ct)
    {
        var leaf = WorkspaceTranscriptLine.MainFileLeaf(ReadTitle(metadata), _shortThreadId);
        if (_leaf is null)
        {
            // Cold start: nothing to rename. A title changed while this process was down is recovered by
            // the watermark probe against the new path, which finds no file and appends everything.
            _leaf = leaf;
            return;
        }

        if (string.Equals(_leaf, leaf, StringComparison.Ordinal))
        {
            return;
        }

        var previous = _leaf;
        var moved = await TryMoveAsync(
            sessionId,
            $"{TranscriptDirectory}/{previous}{TranscriptExtension}",
            $"{TranscriptDirectory}/{leaf}{TranscriptExtension}",
            ct
        ).ConfigureAwait(false);

        if (!moved)
        {
            return;
        }

        if (_agentsDirectoryTouched
            && !await TryMoveAsync(
                sessionId,
                $"{TranscriptDirectory}/{previous}{AgentsDirectorySuffix}",
                $"{TranscriptDirectory}/{leaf}{AgentsDirectorySuffix}",
                ct
            ).ConfigureAwait(false))
        {
            DropSubAgentState();
        }

        _leaf = leaf;
    }

    private void DropSubAgentState()
    {
        foreach (var key in _agentRootParentUids.Keys)
        {
            _ = _watermarks.Remove(key);
        }

        _agentRootParentUids.Clear();
        _agentsDirectoryTouched = false;
    }

    // -------- Sub-agent fan-out --------

    /// <summary>
    ///     Mirrors each descendant thread through the same pipeline, one file per sub-agent.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>STRICTLY SEQUENTIAL — a hard requirement, not a style preference.</b> Every file in this
    ///     conversation is spliced through ONE staged temp path, so two overlapping writes would PUT over
    ///     each other's bytes and <c>cat</c> the survivor into both destinations. Do not "optimise" this
    ///     loop into <c>Task.WhenAll</c>. (Even without that, <c>FileConversationStore</c> serializes every
    ///     read behind one process-wide semaphore, so the parallel version would not be faster.)
    ///     </para>
    ///     <para>
    ///     <b>Bounded per flush</b> for the same reason: a conversation with dozens of descendants would
    ///     otherwise hold that store semaphore for dozens of full-thread reads at every turn boundary. The
    ///     window advances round-robin so a large fan-out is covered across successive flushes instead of
    ///     starving its tail, and a truncated pass reports <c>deferred</c> so another flush follows.
    ///     </para>
    /// </remarks>
    private async Task<(bool Wrote, bool Deferred)> FlushSubAgentsAsync(
        string sessionId,
        string leaf,
        CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _sawSubAgentActivity, 0) == 1)
        {
            _descendants.NoteAgentActivity(ThreadId);
        }

        IReadOnlyList<SubAgentSummary> agents = await _descendants.GetOrScanAsync(ThreadId, ct).ConfigureAwait(false);
        if (agents.Count == 0)
        {
            return (false, false);
        }

        var take = Math.Min(agents.Count, _maxSubAgentFilesPerFlush);
        var capped = take < agents.Count;
        var deferred = false;
        var wrote = false;
        var directory = $"{TranscriptDirectory}/{leaf}{AgentsDirectorySuffix}";

        for (var i = 0; i < take; i++)
        {
            var agent = agents[(_subAgentCursor + i) % agents.Count];
            var messages = await ReadStableAsync(agent.ThreadId, ct).ConfigureAwait(false);
            if (messages is null)
            {
                deferred = true;
                continue;
            }

            if (messages.Count == 0)
            {
                continue;
            }

            var path = $"{directory}/{WorkspaceTranscriptLine.AgentFileLeaf(agent.Name, WorkspaceTranscriptLine.ShortId(agent.AgentId))}{TranscriptExtension}";
            var lines = WorkspaceTranscriptLine.ChainMessages(messages, agent.Name, RootParentUidFor(agent.ThreadId));

            var result = await AppendAsync(sessionId, directory, path, agent.ThreadId, lines, ct).ConfigureAwait(false);
            if (result == AppendResult.Appended)
            {
                wrote = true;
                _agentsDirectoryTouched = true;
            }
            else if (result == AppendResult.Failed)
            {
                deferred = true;
            }
        }

        _subAgentCursor = agents.Count == 0 ? 0 : (_subAgentCursor + take) % agents.Count;

        // A capped pass asks for a follow-up only when it actually mirrored something. Reporting Deferred
        // purely because descendants outnumber the cap would be true on EVERY flush forever, and a caller
        // that re-schedules on Deferred would then spin: the cursor keeps moving but the condition never
        // clears. Gating on progress converges — the first pass that finds its slice already up to date
        // ends the chain.
        return (wrote, deferred || (capped && wrote));
    }

    /// <summary>
    ///     The <c>parent_uid</c> a sub-agent file's first line hangs off: the main file's watermark at the
    ///     moment that file is first written, which is a row that genuinely exists in the main file — so a
    ///     reader resolves the chain across the conversation's whole file set. Pinned per sub-agent so it
    ///     does not drift as the main file grows.
    /// </summary>
    private string? RootParentUidFor(string agentThreadId)
    {
        if (_agentRootParentUids.TryGetValue(agentThreadId, out var pinned))
        {
            return pinned;
        }

        _ = _watermarks.TryGetValue(ThreadId, out var rootUid);
        _agentRootParentUids[agentThreadId] = rootUid;
        return rootUid;
    }

    // -------- Steps 5-7: watermark, write, advance --------

    private enum AppendResult
    {
        UpToDate,
        Appended,
        Failed,
    }

    private async Task<AppendResult> AppendAsync(
        string sessionId,
        string directory,
        string path,
        string watermarkKey,
        IReadOnlyList<WorkspaceTranscriptLine> lines,
        CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return AppendResult.UpToDate;
        }

        var start = await ResolveStartIndexAsync(sessionId, path, watermarkKey, lines, ct).ConfigureAwait(false);
        if (start >= lines.Count)
        {
            return AppendResult.UpToDate;
        }

        var payload = new StringBuilder();
        for (var i = start; i < lines.Count; i++)
        {
            _ = payload.Append(WorkspaceTranscriptLine.Serialize(lines[i])).Append('\n');
        }

        try
        {
            await _fileBrowser
                .WriteWorkspaceFileBytesAsync(sessionId, _tempPath, Encoding.UTF8.GetBytes(payload.ToString()), ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(ex, "Staging transcript bytes for {Path} failed", path);
            return AppendResult.Failed;
        }

        var spliced = await RunAsync(sessionId, ["sh", "-c", AppendScript, "sh", directory, _tempPath, path], ct)
            .ConfigureAwait(false);
        if (spliced is not { ExitCode: 0 })
        {
            // Step 7: the watermark advances ONLY on exit code 0, so the next flush recomputes the same
            // suffix and appends it again. Duplicate-on-retry, never silent loss.
            _logger.LogWarning(
                "Transcript splice into {Path} failed with exit {ExitCode}: {Error}",
                path,
                spliced?.ExitCode ?? -1,
                spliced?.StandardError ?? "(no result)"
            );
            return AppendResult.Failed;
        }

        _watermarks[watermarkKey] = lines[^1].Uid;
        return AppendResult.Appended;
    }

    /// <summary>
    ///     Step 5. Reports the index of the first line that still needs appending — the watermark's
    ///     successor when the watermark is known and present, and otherwise zero.
    /// </summary>
    /// <remarks>
    ///     The fallback is ALWAYS "append everything". Every other candidate (append nothing, append the
    ///     tail, guess by timestamp) trades a duplicate for a permanently missing record, and a duplicate
    ///     is recoverable by any reader that groups on <c>uid</c>.
    /// </remarks>
    private async Task<int> ResolveStartIndexAsync(
        string sessionId,
        string path,
        string watermarkKey,
        IReadOnlyList<WorkspaceTranscriptLine> lines,
        CancellationToken ct)
    {
        if (!_watermarks.TryGetValue(watermarkKey, out var watermark))
        {
            var (hadContent, recovered) = await RecoverWatermarkAsync(sessionId, path, ct).ConfigureAwait(false);
            if (recovered is null)
            {
                if (hadContent)
                {
                    WarnFullAppend(path, "its tail held no readable record");
                }

                return 0;
            }

            watermark = recovered;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Uid, watermark, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        WarnFullAppend(path, $"uid {watermark} is absent from the computed transcript");
        return 0;
    }

    /// <summary>
    ///     Recovers a cold-start watermark from the destination's last few lines.
    /// </summary>
    /// <remarks>
    ///     <c>tail</c> rather than <c>ReadWorkspaceFileBytesAsync</c> on purpose: a GET downloads the WHOLE
    ///     transcript, and these files reach tens of megabytes in practice — paying that once per process
    ///     per conversation to learn eight characters is not a micro-optimisation to skip.
    /// </remarks>
    private async Task<(bool HadContent, string? Uid)> RecoverWatermarkAsync(
        string sessionId,
        string path,
        CancellationToken ct)
    {
        var tailed = await RunAsync(
            sessionId,
            ["tail", "-n", WatermarkTailLines.ToString(CultureInfo.InvariantCulture), "--", path],
            ct
        ).ConfigureAwait(false);

        // A non-zero exit is overwhelmingly "no such file" — the ordinary first-flush case, not a fault.
        if (tailed is not { ExitCode: 0 } || string.IsNullOrWhiteSpace(tailed.StandardOutput))
        {
            return (false, null);
        }

        var window = tailed.StandardOutput.Split('\n');
        for (var i = window.Length - 1; i >= 0; i--)
        {
            if (TryReadUid(window[i]) is { } uid)
            {
                return (true, uid);
            }
        }

        return (true, null);
    }

    private static string? TryReadUid(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("uid", out var uid)
                && uid.ValueKind == JsonValueKind.String
                ? uid.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A torn line. Expected — it is the reason the probe reads a window instead of one line.
            return null;
        }
    }

    private void WarnFullAppend(string path, string reason)
    {
        if (_fullAppendWarned.Add(path))
        {
            _logger.LogWarning("Appending the full transcript to {Path}: {Reason}", path, reason);
        }
    }

    // -------- Step 8: containment --------

    /// <summary>
    ///     Writes <c>.conversations/.gitignore</c> containing <c>*</c>, once per conversation, on the first
    ///     successful flush.
    /// </summary>
    /// <remarks>
    ///     A workspace is frequently a git checkout and an agent frequently runs <c>git add -A</c>. Without
    ///     this, the first such commit publishes this conversation's and its siblings' unredacted reasoning
    ///     off-machine, irrecoverably. Written after the first successful append rather than before, so a
    ///     conversation that never produced a transcript leaves no directory behind.
    /// </remarks>
    private async Task EnsureGitignoreAsync(string sessionId, CancellationToken ct)
    {
        if (_gitignoreWritten)
        {
            return;
        }

        try
        {
            await _fileBrowser
                .WriteWorkspaceFileBytesAsync(
                    sessionId,
                    $"{TranscriptDirectory}/.{GitignoreName}",
                    Encoding.UTF8.GetBytes("*\n"),
                    ct
                )
                .ConfigureAwait(false);
            _gitignoreWritten = true;
        }
        catch (SandboxException ex)
        {
            // Retried on the next flush. The transcript itself is already written; refusing to keep the
            // rows because their ignore file failed would be the wrong trade.
            _logger.LogWarning(ex, "Writing the transcript .gitignore for thread {ThreadId} failed", ThreadId);
        }
    }

    // -------- Gateway helpers --------

    private async Task<bool> TryMoveAsync(string sessionId, string from, string to, CancellationToken ct)
    {
        var result = await RunAsync(sessionId, ["mv", "--", from, to], ct).ConfigureAwait(false);
        if (result is { ExitCode: 0 })
        {
            return true;
        }

        _logger.LogWarning(
            "Renaming transcript {From} to {To} failed with exit {ExitCode}: {Error}",
            from,
            to,
            result?.ExitCode ?? -1,
            result?.StandardError ?? "(no result)"
        );
        return false;
    }

    private async Task<SandboxCommandResult?> RunAsync(
        string sessionId,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        try
        {
            return await _fileBrowser
                .ExecuteWorkspaceCommandAsync(sessionId, new SandboxCommand(arguments), ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(ex, "Transcript command {Command} failed for thread {ThreadId}", arguments[0], ThreadId);
            return null;
        }
    }

    // -------- Metadata --------

    /// <summary>
    ///     Extracts the persisted workspace id, accepting only a genuine string (raw or
    ///     <see cref="JsonElement"/>) exactly as the file-browser route does — a workspace binding is not a
    ///     value to coerce.
    /// </summary>
    private static string? ReadWorkspaceId(ThreadMetadata? metadata) =>
        ReadStringProperty(metadata, MultiTurnAgentPool.WorkspacePropertyKey);

    private static string? ReadTitle(ThreadMetadata? metadata) =>
        ReadStringProperty(metadata, TitlePropertyKey);

    private static string? ReadStringProperty(ThreadMetadata? metadata, string key)
    {
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = value switch
        {
            string raw => raw,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
