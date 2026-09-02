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
///     decision is "should I schedule another flush?", and two members answer yes —
///     <see cref="Deferred"/> and <see cref="Progressing"/>. They are kept DISTINCT because the caller
///     bounds them differently: a deferral means something FAILED and must be retried a bounded number of
///     times, whereas progress means the pass did what it could and a continuation is owed. Folding the
///     second into the first makes an orderly continuation spend a failure budget, which caps how far a
///     single trigger can ever get. <see cref="Unavailable"/> deliberately does NOT ask for a re-schedule
///     — a conversation with no sandbox session would otherwise spin the drain loop forever.
/// </remarks>
public enum TranscriptFlushOutcome
{
    /// <summary>Every file already ended at the current watermark; nothing was appended.</summary>
    UpToDate,

    /// <summary>At least one transcript file was appended to and its watermark advanced.</summary>
    Written,

    /// <summary>
    ///     Something FAILED: a read never settled, a gateway call failed, or the containment
    ///     <c>.gitignore</c> could not be written. The watermarks involved are unadvanced, so a later flush
    ///     repeats the work. <b>Re-schedule, bounded.</b>
    /// </summary>
    Deferred,

    /// <summary>
    ///     There is no workspace session to write into (no workspace bound, no session, credential
    ///     conflict, gateway unreachable). Nothing was written and nothing is pending.
    /// </summary>
    Unavailable,

    /// <summary>
    ///     Nothing failed, and the pass stopped short of the work it knows about: the sub-agent fan-out is
    ///     bounded per flush and descendants outside this slice have not been visited yet.
    ///     <b>Re-schedule, and do NOT count it as a failed attempt.</b> This terminates on its own — each
    ///     pass covers at least one descendant it has not covered before, over a finite roster.
    /// </summary>
    /// <remarks>
    ///     Appended at the END of the enum on purpose: the existing members keep their numeric values, so
    ///     anything that persisted or compared one is unaffected.
    /// </remarks>
    Progressing,
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
///     <b>Every workspace call goes through a shell, and every script text is fixed at compile time.</b>
///     Two scripts change the workspace — <see cref="AppendScript"/>, which splices bytes in, and
///     <see cref="MoveScript"/>, which renames on a retitle — while <see cref="WatermarkProbeScript"/>
///     and <see cref="PathGuardScript"/> read and write nothing. <c>ExecuteWorkspaceCommandAsync</c> is a
///     native argv vector with no implicit shell, so a shell is invoked explicitly; the rename used to be
///     issued as a bare <c>mv</c> argv instead, which left it the one workspace mutation with no guard in
///     scope. <i>No dynamic value is ever interpolated into any script</i>: the destination directory, the
///     temp file, the destination file and the two rename operands all arrive as positional parameters
///     <c>$1</c>/<c>$2</c>/<c>$3</c>. That matters because the file leaf is derived from a user-authored
///     conversation title — it is the positional parameters, not <c>--</c>, that make a title containing
///     <c>$(…)</c> or whitespace inert.
///     </para>
///     <para>
///     <b>No path is written to before it has been checked for redirection, and the check is never
///     cached.</b> Every script guards its own path parameters, and the three writes that bypass the shell
///     entirely — the staged payload, the <c>.gitignore</c> and the tool-result sidecar, all PUT by the
///     gateway — are guarded by <see cref="IsPathSafeAsync"/> immediately before each PUT. The staging
///     guard in particular runs per
///     APPEND rather than per flush: one flush stages the main transcript and every descendant through the
///     same path, with a full gateway round trip between consecutive PUTs. A redirected path is refused and
///     left alone, never unlinked; see <see cref="UnsafePathExitCode"/>.
///     </para>
///     <para>
///     <b>The filesystem surface is a closed set of seven calls</b>, listed here so the next reader can check
///     coverage by enumeration rather than by searching. Four successive review rounds each fixed one
///     surface and left another, because nobody had written down how many there were — and the round that
///     first wrote this list down still recorded a wrong reason for leaving <see cref="TryMoveAsync"/>
///     alone. Named rather than numbered, because inserting an entry renumbers every ordinal after it and
///     turns a cross-reference into a pointer at the wrong row. Members rather
///     than line numbers: a line-number table is stale by the next commit and reads as authoritative anyway.
///     <list type="table">
///         <item>
///             <description><see cref="AdoptExistingLeafAsync"/> — lists the directory. Reads names, never
///             content, and writes nothing; its guard is therefore a SHAPE guard rather than a redirection
///             one, because this is the only call that takes a path component from outside. See
///             <see cref="IsAddressableName"/>.</description>
///         </item>
///         <item>
///             <description><see cref="AppendAsync"/> — PUTs the staged payload. Guarded by
///             <see cref="IsPathSafeAsync"/> per append, uncached; that guard is also where the staging
///             path's link count is checked, because this PUT is the write an in-script check would be
///             too late for.</description>
///         </item>
///         <item>
///             <description><see cref="ExternalizeIfOversizedAsync"/> — PUTs an oversized tool result's
///             sidecar (#254). Guarded by <see cref="IsPathSafeAsync"/> immediately before the PUT, for
///             the same reason as the staged payload: a PUT carries no shell, so an in-script check would
///             run after the bytes were already through. A refusal here INLINES the payload rather than
///             failing the flush — the sidecar is an optimisation of the record's shape, so declining it
///             costs nothing the record needs.</description>
///         </item>
///         <item>
///             <description><see cref="AppendAsync"/> — runs <see cref="AppendScript"/>. Guards its three
///             path parameters in-script, and additionally requires its two FILE operands to be
///             unaliased.</description>
///         </item>
///         <item>
///             <description><see cref="RecoverWatermarkAsync"/> — runs <see cref="WatermarkProbeScript"/>.
///             Guards in-script and refuses an aliased destination before reading it; its output is bounded
///             per line and is never logged.</description>
///         </item>
///         <item>
///             <description><see cref="EnsureGitignoreAsync"/> — PUTs the <c>.gitignore</c>. Guarded by
///             <see cref="IsPathSafeAsync"/>, link count included. This is the write that DESTROYS rather
///             than leaks.</description>
///         </item>
///         <item>
///             <description><see cref="TryMoveAsync"/> — runs <see cref="MoveScript"/>. Guards both operands
///             in-script and additionally refuses a destination that resolves to a directory. It needs no
///             link-count check, and that is a measured exclusion rather than an omission: see
///             <see cref="AliasedPathExitCode"/>.</description>
///         </item>
///     </list>
///     </para>
///     <para>
///     <b>What none of the seven can cover — and a retracted claim about what they could not.</b> An earlier
///     revision of this paragraph asserted that a HARD link at a transcript's name was undetectable here,
///     because <c>[ -L ]</c> cannot see one and "the gateway does not expose <c>st_nlink</c>". The first
///     half is true and the second was never checked. A hard link is indeed not a distinguishable KIND of
///     file — but its LINK COUNT is an ordinary stat field, and the boundary these scripts run against is a
///     shell, which can read it with a POSIX <c>ls -l</c> listing and no extension at all. The same
///     paragraph offered
///     <c>fs.protected_hardlinks</c> as the mitigation, which is wrong in the same direction: that sysctl
///     only refuses links to files the user neither owns nor may write, and the targets that matter here —
///     the tracked sources in the agent's own workspace — are owned by exactly that user. Nothing was
///     stopping it. See <see cref="AliasedPathExitCode"/> for the check that now does.
///     </para>
///     <para>
///     What genuinely remains is smaller and of a different kind. <b>The TOCTOU residue:</b> every check
///     here happens before its write rather than atomically with it, so a link planted in the window is
///     still followed; closing that needs <c>O_NOFOLLOW</c>/<c>O_EXCL</c>, which neither a shell redirection
///     nor the gateway's file PUT exposes. <b>An unreliable <c>st_nlink</c>:</b> a filesystem that reports
///     every file as having one link — some network and FUSE mounts do — makes the alias check pass and
///     protect nothing, silently, with no shell-visible way to tell that apart from a genuinely unaliased
///     file. Both are narrowings rather than closures, and both are stated so the next reader does not have
///     to rediscover them by assuming the opposite.
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

    /// <summary>
    ///     Suffix appended to the main file leaf to form the sibling directory that externalised tool-result
    ///     payloads live in (#254). One per conversation, shared by the main file and every sub-agent file.
    /// </summary>
    /// <remarks>
    ///     Shared rather than per-file because sidecars are keyed by <c>uid</c>, which is derived from the
    ///     persisted row id and so is already unique across the conversation's whole file set. A directory
    ///     per sub-agent would multiply the directories without making a single name safer.
    /// </remarks>
    /// <seealso cref="MoveLeafAsync"/>
    public const string BlobsDirectorySuffix = "_blobs";

    /// <summary>
    ///     Payload size, in bytes, at or above which a TOOL RESULT's content is written to a sidecar and
    ///     referenced from the line instead of being inlined (#254).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>A fixed byte count, not a proportion of the line.</b> Both costs being controlled are
    ///     absolute. A reader streaming the file line-by-line must materialise the largest line whatever
    ///     the rest of the conversation weighs, and the mirror's failure model - a failed append means the
    ///     next flush recomputes and rewrites the same suffix - makes the re-send cost proportional to the
    ///     bytes, not to their share of anything. A relative threshold would also make one row's treatment
    ///     depend on its neighbours, so the same tool result would externalise or not depending on what
    ///     else happened that turn, and a reader could not predict either.
    ///     </para>
    ///     <para>
    ///     64 KiB is chosen to sit well above ordinary tool output - a shell exit, a small file read, a
    ///     search result - and well below the blobs the issue names, so the common conversation writes no
    ///     sidecars at all and pays nothing. It is deliberately not configurable: a threshold that varies
    ///     by deployment makes a durable artifact's shape depend on the host that happened to write it,
    ///     and a reader cannot tell "small enough to inline" from "written by a host with a higher bar".
    ///     </para>
    /// </remarks>
    public const int ToolResultSidecarThresholdBytes = 64 * 1024;

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
    ///     Exit code <see cref="WatermarkProbeScript"/> reports when the destination is DEFINITELY not
    ///     there. Deliberately outside every code the probe's own commands can produce — <c>tail</c>
    ///     answers 0 or 1, <c>sh</c> reserves 126 and 127, and a signalled child reports 128+n — so no
    ///     failure can mint it by accident.
    /// </summary>
    public const int WatermarkMissingExitCode = 42;

    /// <summary>
    ///     Exit code <see cref="WatermarkProbeScript"/> reports when the window was read successfully but
    ///     the destination's last byte is NOT a newline — it ends mid-record, so the final line of the
    ///     window is torn and must not be adopted as a watermark. Chosen on the same grounds as
    ///     <see cref="WatermarkMissingExitCode"/>: outside every code the probe's own commands can mint.
    /// </summary>
    /// <remarks>
    ///     Tornness cannot be re-derived from the bytes any more, because the probe TRUNCATES each line to
    ///     <see cref="WatermarkTailChars"/> characters — every line it returns looks torn. So the shell,
    ///     which is the only party that can still see the whole file, answers the question directly.
    /// </remarks>
    public const int WatermarkPartialExitCode = 43;

    /// <summary>
    ///     Exit code every script reports when a path it was handed — or any directory on the way to it —
    ///     is a SYMLINK, so the write would land somewhere other than where it reads like it lands. Chosen
    ///     on the same grounds as <see cref="WatermarkMissingExitCode"/>: outside every code the scripts'
    ///     own commands can mint.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Not a theoretical hazard. The workspace is shared with the agents working in it, and the
    ///     transcript's file name is derived from the conversation TITLE, so it is guessable long before it
    ///     exists. An agent that drops a symlink at that name redirects the whole unredacted transcript to
    ///     wherever the link points — a tracked source file, say, which is precisely what the
    ///     <c>.gitignore</c> beside the transcript exists to keep it away from. Every shell primitive here
    ///     follows links silently: <c>&gt;&gt;</c> writes through, <c>mkdir -p</c> on a symlinked directory
    ///     succeeds with nothing to do, and <c>[ -e ]</c> and <c>tail</c> report on the target.
    ///     </para>
    ///     <para>
    ///     <b>A redirected path is REFUSED, never repaired.</b> Unlinking it and writing a fresh file would
    ///     destroy whatever it pointed at — the very file the refusal exists to protect — and this code
    ///     cannot tell a hostile link from a deliberate one. So it treats the destination as indeterminate
    ///     and defers, exactly as it does for a probe it could not read. Deferring costs a repeated flush;
    ///     there is no undo for having overwritten someone's source file.
    ///     </para>
    /// </remarks>
    public const int UnsafePathExitCode = 44;

    /// <summary>
    ///     Exit code <see cref="MoveScript"/> reports when a rename's DESTINATION resolves to a directory,
    ///     which is the one shape under which <c>mv</c> writes somewhere other than the name it was given.
    ///     Chosen on the same grounds as <see cref="WatermarkMissingExitCode"/>: outside every code the
    ///     scripts' own commands can mint (GNU <c>mv</c> answers 0 or 1; a BSD <c>mv</c> rejecting an
    ///     option answers 64).
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="UnsafePathExitCode"/> because it is a different fact: the destination
    ///     need not be a symlink at all. A real directory sitting where the transcript's new name goes
    ///     makes <c>mv</c> move the file INSIDE it, which for the <c>_agents</c> rename means nesting the
    ///     old directory under the new one rather than renaming it. Both readings are wrong and both are
    ///     refused, but they are worth telling apart in a log.
    /// </remarks>
    public const int MoveTargetDirectoryExitCode = 45;

    /// <summary>
    ///     Exit code every script reports when a leaf it is about to write — or read as its own staged
    ///     payload — is not a regular file with EXACTLY ONE name. Chosen on the same grounds as
    ///     <see cref="WatermarkMissingExitCode"/>: outside every code the scripts' own commands can mint
    ///     (POSIX <c>find</c> answers 0 or &gt;0).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>A hard link is the same attack as a symlink with none of the tells.</b> The transcript's name
    ///     is derived from the conversation title, so it is guessable long before the file exists; an agent
    ///     that runs <c>ln tracked-file .conversations/&lt;leaf&gt;.jsonl</c> gives a tracked source file a
    ///     second name inside the mirror's directory. Every check in
    ///     <see cref="PathGuardPreamble"/> then passes — the entry is not a symlink, no ancestor is, and it
    ///     is a perfectly ordinary regular file — and <c>cat "$2" &gt;&gt; "$3"</c> appends the unredacted
    ///     transcript into the shared inode, which is to say into the tracked file, outside the reach of the
    ///     <c>.gitignore</c> that never covered it. Directly observed rather than inferred: with
    ///     <c>ln tracked.txt aliased.jsonl</c>, <c>[ -L aliased.jsonl ]</c> is false and
    ///     <c>printf 'SECRET\n' &gt;&gt; aliased.jsonl</c> puts SECRET in <c>tracked.txt</c>.
    ///     </para>
    ///     <para>
    ///     <b>The link count is the whole of the check, because there is nothing else to look at.</b> A hard
    ///     link is not a kind of file and has no target; both names are equally the file, and the
    ///     filesystem does not record which came first. So "is this OUR transcript that someone aliased, or
    ///     THEIR file we were pointed at?" is not a question that can be answered here, in principle rather
    ///     than for want of a syscall — which is exactly why the answer is to refuse either way rather than
    ///     to repair, on the same reasoning as <see cref="UnsafePathExitCode"/>. Unlinking the extra name
    ///     would be indistinguishable from deleting a file the workspace owner meant to keep.
    ///     </para>
    ///     <para>
    ///     <b>The availability consequence is real and is the accepted cost.</b> Once a transcript exists,
    ///     anyone with write access to the workspace can <c>ln</c> to IT and drive the count to two, after
    ///     which every append is refused, the flush defers, the caller's retry budget is spent and that
    ///     conversation's mirror simply stops. A backup or checkout tool that hard-links (<c>cp -l</c>,
    ///     <c>rsync --link-dest</c>) does the same by accident. This is the correct direction to fail — a
    ///     stalled transcript is recoverable by removing the extra link, an unredacted transcript welded
    ///     into a tracked file is not — but it is a behaviour change rather than a pure hardening, and a
    ///     transcript that has silently stopped is something an operator will otherwise debug from the
    ///     wrong end. Hence a code of its own rather than sharing
    ///     <see cref="UnsafePathExitCode"/>, and a log line that names the cause.
    ///     </para>
    ///     <para>
    ///     <b>Which surfaces need it, and which measurably do not.</b> The two gateway PUTs need it MOST and
    ///     could not get it from a script guard at all: the payload and the <c>.gitignore</c> are written
    ///     before any script for that write runs, so by the time <see cref="AppendScript"/> could inspect
    ///     the staging path the PUT has already gone through it — a <c>.gitignore</c> PUT through an aliased
    ///     path replaces a tracked file's whole content with <c>*</c>. They are covered in
    ///     <see cref="IsPathSafeAsync"/>, which runs immediately before each PUT.
    ///     <see cref="MoveScript"/> is the exclusion: <c>rename(2)</c> unlinks the destination NAME and does
    ///     not write through it, so an aliased destination loses only its extra name and the file itself is
    ///     untouched. Verified rather than assumed — <c>ln tracked.txt new.jsonl</c> followed by
    ///     <c>mv -T old.jsonl new.jsonl</c> leaves <c>tracked.txt</c> byte-identical, at link count one.
    ///     </para>
    /// </remarks>
    public const int AliasedPathExitCode = 46;

    /// <summary>
    ///     How many leading characters of each windowed line the probe returns. This is what bounds the
    ///     probe's output, and bounding it is not an optimisation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     A single transcript line carries a whole message, and on this revision a tool result is stored
    ///     inline, so one line reaches MEGABYTES. Returning five of those through the gateway's command
    ///     channel exceeds its output cap; the SDK throws, <c>RunAsync</c> reports the transport fault as
    ///     an indeterminate probe, and the flush defers. A cold start re-probes every time, so it defers
    ///     again, and again, until the retry budget is spent — the transcript is then never mirrored at
    ///     all. Capping per line makes the probe's output a small CONSTANT (<see cref="WatermarkTailLines"/>
    ///     x this, plus newlines) no matter how large the file or its lines.
    ///     </para>
    ///     <para>
    ///     The cap is safe because <c>WorkspaceTranscriptLine.Serialize</c> pins the key ORDER, writing
    ///     <c>schema_version</c>, <c>type</c> and then <c>uid</c> — so the uid always ends within roughly
    ///     the first 53 bytes of a line, whatever follows it. 256 leaves a wide margin for that layout to
    ///     grow without anyone having to remember this constant exists.
    ///     </para>
    /// </remarks>
    public const int WatermarkTailChars = 256;

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
    ///     Defines the two shell functions every script opens with. <c>guard</c> walks a path and each of
    ///     its ancestor directories and fails if ANY of them is a symlink; <c>solo</c> answers whether an
    ///     EXISTING path is a regular file bearing exactly one name. Prepended, never interpolated into.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Walking the ancestors is the half that is easy to leave out and impossible to do without. A
    ///     symlinked <c>.conversations</c> redirects every file beneath it — the transcripts and the
    ///     <c>.gitignore</c> that is supposed to be covering them — while the leaf itself tests perfectly
    ///     clean, because at that point the leaf does not exist yet. <c>mkdir -p</c> reports success on a
    ///     symlinked directory precisely because there is nothing left for it to create.
    ///     </para>
    ///     <para>
    ///     <c>return</c>, not <c>exit</c>: an <c>exit</c> inside a function ends the whole script, so the
    ///     caller could not attach <see cref="UnsafePathExitCode"/> to the failure. The trailing
    ///     <c>return 0</c> is likewise load-bearing — without it the function's status is that of the last
    ///     <c>[ -L ]</c> it ran, which is 1 for every safe path.
    ///     </para>
    ///     <para>
    ///     <b><c>solo</c> exists because <c>guard</c> cannot see a hard link at all</b> — see
    ///     <see cref="AliasedPathExitCode"/> for what that costs. A MISSING path passes: these paths are
    ///     routinely written before they exist, and the check is about the file that is there, not about
    ///     the name. <c>[ -f ]</c> ahead of the count is doing real work rather than tidying — it rejects a
    ///     destination that has become a directory, whose link count is at least two by construction and
    ///     would otherwise be reported as an alias, which is true but for the wrong reason and would read
    ///     as one in the log.
    ///     </para>
    ///     <para>
    ///     <b>The link count comes from <c>ls</c>, and the two more obvious primitives were both tried and
    ///     rejected on evidence.</b> <c>stat</c> is out because the format string is not portable —
    ///     <c>%h</c> is GNU and <c>%l</c> is BSD — and these scripts are compile-time constants shared by
    ///     two very different executions: the sandbox container in production, and whatever <c>sh</c> the
    ///     developer's machine offers under test. POSIX <c>find … -links 1</c> looked like the portable
    ///     answer and is worse: on a Windows host the shell resolves <c>find</c> to
    ///     <c>C:\WINDOWS\system32\find.exe</c>, an unrelated text-search tool that prints
    ///     "FIND: Parameter format not correct" and no path — indistinguishable from a legitimate refusal,
    ///     so every append on such a host is declined. Directly observed, which is the only reason it was
    ///     not shipped. <c>ls -dn</c> has no such collision, and the second field of a <c>-l</c> listing is
    ///     the link count by POSIX definition (<c>-n</c> turns on <c>-l</c>), so the format is specified
    ///     rather than incidental.
    ///     </para>
    ///     <para>
    ///     <c>solo</c> is fail-closed by construction rather than by an added check. A failing <c>ls</c>
    ///     returns 1 outright, and any output that is not a long listing leaves field two something other
    ///     than <c>1</c> — so every way the test can go wrong, including <c>ls</c> being absent, comes out
    ///     as a refusal. The one direction it can be wrong in silently is a filesystem that reports an
    ///     unreliable <c>st_nlink</c> (some network and FUSE mounts pin it to 1), where an aliased file
    ///     would pass; nothing readable from a shell distinguishes that case.
    ///     </para>
    ///     <para>
    ///     This narrows the window rather than closing it: a link planted between the guard and the write
    ///     is still followed. Closing it needs <c>O_NOFOLLOW</c>, which neither the gateway's file PUT nor
    ///     a POSIX shell redirection exposes. The residue is a race an attacker must win against a
    ///     millisecond; what it replaces is a link that could be planted at leisure, days ahead, and
    ///     would be followed with certainty.
    ///     </para>
    /// </remarks>
    private const string PathGuardPreamble =
        "guard() { p=\"$1\"; while [ -n \"$p\" ] && [ \"$p\" != \".\" ] && [ \"$p\" != \"/\" ]; do "
        + "if [ -L \"$p\" ]; then return 1; fi; p=$(dirname \"$p\"); done; return 0; }\n"
        + "solo() { [ -e \"$1\" ] || return 0; [ -f \"$1\" ] || return 1; "
        + "sl=$(ls -dn \"$1\") || return 1; set -- $sl; [ \"$2\" = \"1\" ]; }\n";

    /// <summary>
    ///     One guard invocation for the given positional parameter, reporting
    ///     <see cref="UnsafePathExitCode"/>. A method rather than three literals so the exit code cannot
    ///     drift between the parameters.
    /// </summary>
    private static string GuardLine(string parameter) =>
        "guard \"" + parameter + "\" || exit " + UnsafePathExitCode.ToString(CultureInfo.InvariantCulture) + "\n";

    /// <summary>
    ///     One <c>solo</c> invocation for the given positional parameter, reporting
    ///     <see cref="AliasedPathExitCode"/>. Paired with <see cref="GuardLine"/> for the same
    ///     no-drift reason, and always written AFTER it: a symlinked ancestor makes the link count of the
    ///     leaf a fact about somebody else's file, so "not redirected" has to be settled first.
    /// </summary>
    private static string SoloLine(string parameter) =>
        "solo \"" + parameter + "\" || exit " + AliasedPathExitCode.ToString(CultureInfo.InvariantCulture) + "\n";

    /// <summary>
    ///     The guards on their own, for the three writes that reach the workspace WITHOUT a shell — the
    ///     staged payload, the <c>.gitignore</c> and the tool-result sidecar (#254), all of which the
    ///     gateway PUTs directly. <c>$1</c> is the path. Reads nothing, writes nothing. See
    ///     <see cref="IsPathSafeAsync"/>.
    /// </summary>
    /// <remarks>
    ///     This is the ONLY place the alias check can defend those writes, and it is why
    ///     <see cref="AliasedPathExitCode"/> belongs here rather than only on the append: a PUT carries no
    ///     shell, so by the time <see cref="AppendScript"/> could look at the staging path the bytes are
    ///     already through it. The <c>.gitignore</c> is the sharpest of the three — it is PUT with the whole
    ///     file's content, so an aliased path does not append to a tracked file, it REPLACES one with
    ///     <c>*</c>.
    /// </remarks>
    private static readonly string PathGuardScript = PathGuardPreamble + GuardLine("$1") + SoloLine("$1");

    /// <summary>
    ///     THE one shell call site that writes. Built from concatenated literals rather than a raw string
    ///     literal so the line endings are LF regardless of how this source file is checked out — a script
    ///     carrying CR bytes fails inside the container in ways that are tedious to diagnose.
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
    ///     <para>
    ///     All three parameters are guarded, not just the destination. <c>$3</c> is the obvious one —
    ///     <c>&gt;&gt;</c> writes straight through a link. <c>$1</c> catches the redirected directory that
    ///     <c>mkdir -p</c> would silently accept, which is how a sub-agent's whole transcript leaves
    ///     through a symlinked <c>_agents</c> folder. <c>$2</c> is guarded because <c>cat</c> reads through
    ///     a link too, and a staged payload that is really a pointer at some other file would splice that
    ///     file's contents into the transcript. See <see cref="UnsafePathExitCode"/>.
    ///     </para>
    ///     <para>
    ///     <c>solo</c> covers the same two FILE parameters and deliberately not <c>$1</c>: a directory's
    ///     link count is at least two by construction (itself and <c>.</c>), so applying it there would
    ///     refuse every append that has ever worked. <c>$3</c> is the destination the hard-link attack aims
    ///     at; <c>$2</c> is symmetric with its symlink guard, and closes the narrower window in which the
    ///     staged payload is aliased AFTER the PUT that <see cref="IsPathSafeAsync"/> cleared. See
    ///     <see cref="AliasedPathExitCode"/>.
    ///     </para>
    /// </remarks>
    private static readonly string AppendScript =
        PathGuardPreamble
        + GuardLine("$1")
        + GuardLine("$2")
        + GuardLine("$3")
        + SoloLine("$2")
        + SoloLine("$3")
        + "mkdir -p \"$1\" || exit 1\n"
        + "if [ -s \"$3\" ] && [ -n \"$(tail -c1 \"$3\")\" ]; then printf '\\n' >> \"$3\" || exit 1; fi\n"
        + "cat \"$2\" >> \"$3\" && rm -f \"$2\"\n";

    /// <summary>
    ///     The cold-start watermark probe. <c>$1</c> destination file, <c>$2</c> trailing line count,
    ///     <c>$3</c> per-line character cap. Reads nothing and writes nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     The existence test is the entire point of running this through a shell instead of invoking
    ///     <c>tail</c> directly. <c>tail</c> answers a missing file, an unreadable one and an I/O error with
    ///     the SAME status 1, and its stderr wording is locale-dependent, so "there is no transcript" is not
    ///     a conclusion its exit code can support. Guessing it wrong is expensive in one direction only:
    ///     the caller reads "no transcript", starts from row zero and re-appends the ENTIRE history onto a
    ///     file that already holds it. So the script decides existence itself with <c>[ -e ]</c> and
    ///     reports that one answer through <see cref="WatermarkMissingExitCode"/>; every other non-zero
    ///     status keeps its ordinary meaning — an indeterminate failure the caller must DEFER on.
    ///     </para>
    ///     <para>
    ///     <b><c>cut -c</c> is what keeps the probe's output a small constant</b> — see
    ///     <see cref="WatermarkTailChars"/> for the wedge it prevents. It truncates per LINE, after
    ///     <c>tail</c> has chosen the lines: a plain byte budget over the whole window
    ///     (<c>tail -c</c>, or <c>head -c</c> downstream) is not equivalent, because one multi-megabyte
    ///     line would swallow the entire budget and leave no uid to find. <c>cut</c> is POSIX and present
    ///     in the sandbox image (<c>mcr.microsoft.com/dotnet/sdk:10.0-noble</c>, GNU coreutils).
    ///     </para>
    ///     <para>
    ///     Truncating costs the ability to tell a torn last line from an intact one by parsing, so the
    ///     LAST line answers that separately through <see cref="WatermarkPartialExitCode"/>, using the same
    ///     <c>tail -c1</c> idiom <see cref="AppendScript"/> already welds newlines with. It is read AFTER
    ///     the window on purpose: a concurrent append landing between the two reads can then only make the
    ///     probe MORE conservative (an intact window reported partial re-appends one row the reader dedupes
    ///     on uid), never less (a torn window reported intact would adopt a torn uid and lose rows).
    ///     </para>
    ///     <para>
    ///     A race between the test and the window read is harmless in the direction that matters: a file
    ///     created in between makes <c>tail</c> succeed, and one deleted in between makes it exit 1, which
    ///     is read as indeterminate and defers. The reverse — a real absence read as a failure — costs one
    ///     repeated flush, never a duplicated transcript.
    ///     </para>
    ///     <para>
    ///     Built from concatenated literals for the same LF reason as <see cref="AppendScript"/>, and the
    ///     title-derived path arrives as a positional parameter for the same inertness reason. Both exit
    ///     codes are spliced in from their constants rather than typed twice, so they cannot drift.
    ///     </para>
    ///     <para>
    ///     The guard runs first because <c>[ -e ]</c> and <c>tail</c> both report on a link's TARGET. A
    ///     redirected destination would otherwise be probed as though it were the transcript, and the
    ///     watermark read out of some unrelated file would decide how much of the history to append into
    ///     it. Reporting <see cref="UnsafePathExitCode"/> lands in the same "indeterminate" branch as any
    ///     other unexpected status, which is the correct reading: nothing about that path is known.
    ///     </para>
    ///     <para>
    ///     <c>solo</c> is here for a reason of COST rather than of confidentiality — this probe reads, and
    ///     an aliased destination is going to be refused at the append regardless. But the watermark is
    ///     only recorded after a SUCCESSFUL append, so while the destination stays aliased the probe re-runs
    ///     from cold on every flush, and each of those doomed attempts stages the entire unredacted history
    ///     into <c>.conversations/.tmp/</c> through a gateway PUT before the append declines it. Refusing at
    ///     the probe collapses that to one cheap shell call. See <see cref="AliasedPathExitCode"/>.
    ///     </para>
    /// </remarks>
    private static readonly string WatermarkProbeScript =
        PathGuardPreamble
        + GuardLine("$1")
        + SoloLine("$1")
        + "[ -e \"$1\" ] || exit "
        + WatermarkMissingExitCode.ToString(CultureInfo.InvariantCulture)
        + "\n"
        + "tail -n \"$2\" -- \"$1\" | cut -c \"1-$3\" || exit 1\n"
        + "end=$(tail -c1 -- \"$1\") || exit 1\n"
        + "[ -z \"$end\" ] || exit "
        + WatermarkPartialExitCode.ToString(CultureInfo.InvariantCulture)
        + "\n";

    /// <summary>
    ///     The retitle rename. <c>$1</c> is the existing path, <c>$2</c> the new one. It is a shell script
    ///     rather than a bare <c>mv</c> argv because a rename needs the same guard every other path here
    ///     gets, and a native argv vector has nowhere to put one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b><c>mv A B</c> does NOT simply replace the path <c>B</c>, and an earlier revision left this
    ///     call unguarded on the belief that it did. That belief was wrong for the case that matters.</b>
    ///     When <c>B</c> resolves to a DIRECTORY — including through a symlink — <c>mv</c> moves <c>A</c>
    ///     INSIDE it. A link planted at the transcript's next name therefore relocates the whole
    ///     unredacted file out of <c>.conversations</c> and out from under the <c>.gitignore</c>, at exit
    ///     code 0, with nothing left behind to notice. Directly observed, not inferred:
    ///     <c>mv -- covered/old.jsonl covered/new.jsonl</c> with <c>covered/new.jsonl</c> a symlink to a
    ///     directory leaves the transcript at <c>&lt;target&gt;/old.jsonl</c> and the link untouched. The
    ///     <c>_agents</c> rename has the identical shape and the identical exposure.
    ///     </para>
    ///     <para>
    ///     A symlink to a FILE is a different matter and never was the hazard: <c>rename(2)</c> replaces
    ///     the link itself, so the link's target is not written through. Resolving to a directory is the
    ///     whole of it, which is why <c>[ ! -d ]</c> is an exact test rather than a conservative one.
    ///     </para>
    ///     <para>
    ///     <b>The hard-link check the other three scripts carry is deliberately absent here, on the same
    ///     reasoning and with the same kind of evidence.</b> A rename unlinks the destination NAME; it does
    ///     not open the file behind it, so an aliased destination loses one of its names and nothing is
    ///     written into the shared inode. Observed rather than assumed: with <c>new.jsonl</c> a hard link to
    ///     a tracked file, <c>mv -T -- old.jsonl new.jsonl</c> exits 0, leaves the tracked file
    ///     byte-identical, and drops its link count back to one. Adding <c>solo</c> here would buy nothing
    ///     and would hand every retitle a new way to fail. See <see cref="AliasedPathExitCode"/>.
    ///     </para>
    ///     <para>
    ///     <c>mv -T</c> (<c>--no-target-directory</c>) is tried FIRST because it treats the destination as
    ///     a name rather than a directory, which closes the window between the check and the move instead
    ///     of merely narrowing it: against the same fixture it renames onto the link, leaves the link's
    ///     target empty, and reports 0. It is a GNU extension, present in the sandbox image
    ///     (<c>mcr.microsoft.com/dotnet/sdk:10.0-noble</c>, GNU coreutils) but absent from a BSD
    ///     <c>mv</c> — and these scripts also run against a developer machine's own <c>sh</c> under test,
    ///     so an unconditional <c>-T</c> would wedge every retitle on such a host. Hence the fallback.
    ///     </para>
    ///     <para>
    ///     <b>The fallback is safe only because of the <c>target</c> check, and only because that check is
    ///     re-run ahead of it.</b> A bare <c>mv -T … || mv …</c> is a trap: <c>mv -T</c> also fails on a
    ///     destination that is a real NON-EMPTY directory, and the plain <c>mv</c> behind it would then
    ///     nest the source inside — which is precisely the <c>_agents</c> case. Refusing a directory
    ///     destination outright removes that branch, and repeating the check keeps the fallback's exposure
    ///     no wider than the primary's.
    ///     </para>
    ///     <para>
    ///     What remains is the same residual TOCTOU every other path here carries: a link planted between
    ///     the check and the <c>mv</c> is still followed on a host without <c>-T</c>. Closing it needs
    ///     <c>O_NOFOLLOW</c>/<c>renameat2</c>, which no shell exposes. See <see cref="UnsafePathExitCode"/>
    ///     for why the answer is refusal rather than repair — the link is never unlinked, and a refused
    ///     rename simply keeps the transcript under its old name, which <see cref="MoveLeafAsync"/> already
    ///     treats as the safe outcome.
    ///     </para>
    /// </remarks>
    private static readonly string MoveScript =
        PathGuardPreamble
        + "target() { guard \"$1\" || exit "
        + UnsafePathExitCode.ToString(CultureInfo.InvariantCulture)
        + "; [ ! -d \"$1\" ] || exit "
        + MoveTargetDirectoryExitCode.ToString(CultureInfo.InvariantCulture)
        + "; }\n"
        + GuardLine("$1")
        + "target \"$2\"\n"
        + "mv -T -- \"$1\" \"$2\" 2>/dev/null && exit 0\n"
        + "target \"$2\"\n"
        + "mv -- \"$1\" \"$2\"\n";

    /// <summary>
    ///     The one property <see cref="TryReadUid"/> looks for, as UTF-8 so the reader can compare it
    ///     without materialising the name. Must match the key <c>WorkspaceTranscriptLine.Serialize</c>
    ///     writes.
    /// </summary>
    private static ReadOnlySpan<byte> UidPropertyName => "uid"u8;

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

    /// <summary>
    ///     The descendants the CURRENT coverage sweep has already visited. A capped fan-out asks for a
    ///     follow-up only while some descendant is missing from this set, which is what makes the chain of
    ///     continuations terminate without spending the caller's failure budget.
    /// </summary>
    private readonly HashSet<string> _sweptAgents = new(StringComparer.Ordinal);

    private string? _leaf;
    private bool _gitignoreWritten;

    /// <summary>
    ///     Whether this conversation has a transcript in the workspace at all — set when an append lands
    ///     and when the cold-start probe finds an existing file. Distinct from
    ///     <see cref="_gitignoreWritten"/> and from "this flush wrote something": it is what lets
    ///     <see cref="EnsureContainedAsync"/> be attempted on a flush that appends nothing, for a
    ///     transcript an earlier process left behind.
    /// </summary>
    private bool _transcriptExists;

    private bool _agentsDirectoryTouched;

    /// <summary>
    ///     Whether this writer has already reported the sandbox SDK failing to release a command's gateway
    ///     operation record. One writer serves one conversation (and therefore one workspace session), so
    ///     this makes that report once per session at Warning and per call at Debug thereafter. A retained
    ///     record keeps its slot in the gateway's per-session record cap, and a session that retains one
    ///     per command eventually gets every flush refused with 503 <c>operation_capacity_exhausted</c>
    ///     (issue #725) — the signal is worth surfacing, but warning on every flush is exactly how that
    ///     failure buried itself in the host log the first time.
    /// </summary>
    private bool _unreleasedOperationRecordReported;

    private int _subAgentCursor;
    private int _sawSubAgentActivity;
    private int _sweepRestartRequested;

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
        int maxSubAgentFilesPerFlush = DefaultMaxSubAgentFilesPerFlush
    )
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
    ///     Records that an independent external trigger arrived (a completed run, a sub-agent
    ///     notification, a recovered subscription), so the next flush starts a FRESH coverage sweep of the
    ///     descendant roster rather than continuing the one the previous trigger left half-finished.
    /// </summary>
    /// <remarks>
    ///     Deliberately separate from <see cref="NoteSubAgentActivity"/>. A sweep restart is cheap — it
    ///     clears a set — whereas noting sub-agent activity forces the descendant scanner's full store
    ///     scan, which must stay rare. Every trigger owes a fresh sweep; only sub-agent evidence owes a
    ///     rescan. Restarting on an external trigger rather than whenever a sweep completes is also what
    ///     stops the writer alternating forever between "sweep finished" and "sweep restarted" on a
    ///     conversation where nothing is happening. Cheap and thread-safe: callable straight from the
    ///     message-subscriber hot path.
    /// </remarks>
    public void NoteExternalTrigger() => Interlocked.Exchange(ref _sweepRestartRequested, 1);

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
        // after the append leaves a second file holding a duplicate of the entire history. It reports
        // false when the workspace could not be INSPECTED, which is not the same as finding nothing —
        // see AdoptExistingLeafAsync.
        if (!await ApplyRetitleAsync(sessionId, metadata, ct).ConfigureAwait(false))
        {
            return TranscriptFlushOutcome.Deferred;
        }

        var leaf = _leaf!;

        var lines = WorkspaceTranscriptLine.ChainMessages(messages);

        // Containment, first line. The load-bearing one is inside AppendAsync, which every byte of every
        // file goes through; this one additionally covers a transcript an earlier process left behind,
        // which may never be appended to at all. See EnsureContainedAsync.
        if ((lines.Count > 0 || _transcriptExists) && !await EnsureContainedAsync(sessionId, ct).ConfigureAwait(false))
        {
            return TranscriptFlushOutcome.Deferred;
        }

        var mainResult = await AppendAsync(
                sessionId,
                TranscriptDirectory,
                $"{TranscriptDirectory}/{leaf}{TranscriptExtension}",
                ThreadId,
                lines,
                ct
            )
            .ConfigureAwait(false);

        if (mainResult == AppendResult.Failed)
        {
            // The fan-out is SKIPPED, not merely reported alongside a failed main append. A sub-agent
            // file's first line anchors to the main file's watermark, and RootParentUidFor pins whatever
            // it reads the first time it is asked — permanently, for the life of this writer. Running the
            // fan-out here would mint that anchor from a watermark this failed append left unadvanced, so
            // on a FIRST flush every sub-agent file would be pinned to null and the two files would never
            // be one lineage again. No later flush revisits a pinned anchor. Deferring costs one repeated
            // pass; a wrong anchor is not recoverable.
            return TranscriptFlushOutcome.Deferred;
        }

        var wrote = mainResult == AppendResult.Appended;
        var (agentsWrote, agentsDeferred, agentsProgressing) = await FlushSubAgentsAsync(sessionId, leaf, ct)
            .ConfigureAwait(false);
        wrote = wrote || agentsWrote;

        // Deferred outranks Progressing: a failure has to be retried under a bound whether or not the
        // sweep also has ground left to cover, and the continuation the sweep wants comes for free with
        // the retry. Progressing outranks Written/UpToDate — both of those tell the caller to stop.
        return agentsDeferred ? TranscriptFlushOutcome.Deferred
            : agentsProgressing ? TranscriptFlushOutcome.Progressing
            : wrote ? TranscriptFlushOutcome.Written
            : TranscriptFlushOutcome.UpToDate;
    }

    /// <summary>
    ///     Makes sure this conversation's transcript directory is covered by a <c>.gitignore</c>, reporting
    ///     false when it is not. <b>Every caller must treat false as "write nothing".</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Containment is established BEFORE any transcript byte reaches the workspace, and this is the
    ///     highest-consequence rule in the type.</b> This <c>.gitignore</c> is the entire opt-out for the
    ///     feature: without it the next <c>git add -A</c> an agent runs in that workspace publishes the
    ///     conversation's unredacted reasoning off-machine, irrecoverably. Ordering it first makes
    ///     "transcript on disk, uncovered" a state the writer cannot be left in — not by a failed write,
    ///     not by an exhausted retry budget, not by a process that exits between the two calls. A
    ///     conversation that never gets its ignore file simply never gets a transcript either, which is
    ///     the safe side of the trade.
    ///     </para>
    ///     <para>
    ///     Writing it AFTERWARDS cannot be repaired by retrying harder, and that is why the ordering
    ///     rather than the retry is the fix. The append advances the watermark on success, so by the time
    ///     a failed containment is discovered the bytes are already there; the retry then depends on a
    ///     later flush arriving, and for a conversation whose last turn has happened none ever does.
    ///     </para>
    ///     <para>
    ///     <b>The load-bearing call is inside <c>AppendAsync</c></b> — the one path every transcript byte
    ///     passes through, main file and sub-agent file alike — so the rule holds by construction rather
    ///     than by each call site remembering it. Enforcing it only at the top of the flush was not enough,
    ///     and the gap was not merely a narrow race: both of the flush's brakes are keyed to the MAIN
    ///     conversation, while the bytes at stake can belong to a DESCENDANT. A root with no stable rows
    ///     and no known transcript answers "no" to <c>lines.Count > 0 || _transcriptExists</c> and so skips
    ///     the check; its own append then reports <c>UpToDate</c> rather than <c>Failed</c>, so the guard
    ///     that skips the fan-out on a failed main append does not fire either; and the fan-out writes
    ///     sub-agent transcripts into an uncovered directory. A sub-agent's reasoning is no less
    ///     publishable than the root's.
    ///     </para>
    ///     <para>
    ///     The flush-level check is KEPT, as a first line rather than the only one, because it still covers
    ///     what an append cannot: a transcript an EARLIER process left in the workspace, which this writer
    ///     may adopt with nothing further to append and therefore never append to at all. Its
    ///     <c>lines.Count > 0 || _transcriptExists</c> condition cannot be wrong in the unsafe direction —
    ///     rows in hand means an append is about to happen, and no rows plus no known transcript means
    ///     there is nothing of this conversation's own in the workspace to cover — and gating on it is what
    ///     keeps a conversation that never produces a transcript from leaving a directory behind.
    ///     </para>
    /// </remarks>
    private async Task<bool> EnsureContainedAsync(string sessionId, CancellationToken ct) =>
        _gitignoreWritten || await EnsureGitignoreAsync(sessionId, ct).ConfigureAwait(false);

    // -------- Step 1: session --------

    /// <summary>
    ///     Resolves the conversation's workspace session, or reports null for every reason a background
    ///     flush legitimately has none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     A flush has no inbound HTTP request and therefore no caller credential — so it resolves
    ///     through the background seam, which skips the registry's cross-actor provenance comparison
    ///     rather than presenting a null credential to it.
    ///     </para>
    ///     <para>
    ///     <b>That distinction is the whole of #253.</b> Provenance is compared raw and nullable, and
    ///     <c>null</c> means "the interactive UI" — a provenance, not an absence. Passing null from here
    ///     therefore did not say "no caller", it said "the UI caller", which conflicts with an S2S-owned
    ///     binding exactly as a foreign app would. An S2S-created conversation reported
    ///     <c>CredentialConflict</c> on every flush, forever, with no input that could ever change; ADR
    ///     0011 recorded the resulting silence as accepted gap 3. The mirror is not a caller at all: it is
    ///     the host writing this thread's own record into this thread's own workspace, and it holds no
    ///     credential to be checked. Saying so is what fixed it.
    ///     </para>
    ///     <para>
    ///     <c>CredentialConflict</c> is still handled below, and still reachable: a binding whose owner
    ///     the registry cannot resolve, or a future caller-bearing path through this method, must not
    ///     silently fall through to "no session".
    ///     </para>
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
                .ResolveThreadWorkspaceSessionForBackgroundAsync(ThreadId, workspaceId, ct)
                .ConfigureAwait(false);
        }
        catch (SandboxSessionUnavailableException ex)
        {
            // The gateway could not recreate an idle-evicted session. Expected background weather, not an
            // error the operator needs to see once per turn.
            _logger.LogDebug(
                ex,
                "Sandbox session unavailable for thread {ThreadId}; transcript flush skipped",
                ThreadId
            );
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
    ///     Notices a title change and renames the conversation's file (and, when there is one, its
    ///     sub-agent directory) before anything is appended this flush — including a title change that
    ///     happened while this process was not running.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Adoption of the new leaf is gated on the FILE move succeeding: on failure the flush keeps
    ///     writing to the old path — which a failed <c>mv</c> left in place, complete and readable — and
    ///     retries next time.
    ///     </para>
    ///     <para>
    ///     The directory move is attempted only after the file move succeeded and only when there is a
    ///     directory to move. A conversation with no sub-agents has none, and issuing the <c>mv</c> anyway
    ///     would fail every time and wedge the rename. If the directory move does fail, the in-memory
    ///     sub-agent watermarks describe files that did not move, so they are dropped: the directory is
    ///     rebuilt in full under the new name (duplicate rows, which dedupe on <c>uid</c>) rather than
    ///     silently resuming past rows the new files do not contain.
    ///     </para>
    ///     <para>
    ///     <b>Cold start is a retitle too.</b> A title edited while the host was down is invisible to this
    ///     writer's in-memory state, so simply adopting the computed leaf produced a SECOND transcript for
    ///     the same conversation — the old file frozen at its last row under the old slug, the new one
    ///     starting the history again from zero, plus an orphaned <c>_agents/</c> directory beside the
    ///     wrong name. The short id in the leaf is what makes the old file findable: it is stable across
    ///     any number of retitles, so a directory listing identifies the conversation's own file under
    ///     whatever slug it was last named, and the same move path adopts it.
    ///     </para>
    /// </remarks>
    private async Task<bool> ApplyRetitleAsync(string sessionId, ThreadMetadata? metadata, CancellationToken ct)
    {
        var leaf = WorkspaceTranscriptLine.MainFileLeaf(ReadTitle(metadata), _shortThreadId);
        if (_leaf is null)
        {
            var (listed, adopted) = await AdoptExistingLeafAsync(sessionId, leaf, ct).ConfigureAwait(false);
            if (!listed)
            {
                return false;
            }

            _leaf = adopted ?? leaf;
            return true;
        }

        if (string.Equals(_leaf, leaf, StringComparison.Ordinal))
        {
            return true;
        }

        if (await MoveLeafAsync(sessionId, _leaf, leaf, _agentsDirectoryTouched, ct).ConfigureAwait(false))
        {
            _leaf = leaf;
        }

        return true;
    }

    /// <summary>
    ///     On cold start, finds this conversation's existing transcript under a DIFFERENT slug and renames
    ///     it (and its sub-agent directory) onto <paramref name="leaf"/>.
    /// </summary>
    /// <returns>
    ///     <c>Listed</c> is whether the workspace could be inspected at all; when it is false the caller
    ///     must abandon the flush without writing anything. <c>Leaf</c> is the leaf actually in force
    ///     afterwards, or null when the listing succeeded and there was nothing to adopt.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///     <b>"The directory holds nothing" and "the directory could not be listed" must be answered
    ///     differently, and only the first is a definite miss.</b> Treating a transport, protocol or
    ///     session failure as an absence makes the writer adopt the computed leaf and append the entire
    ///     history into a SECOND file — the split-transcript defect this method exists to prevent, except
    ///     triggered by a momentary gateway fault instead of a real absence, and unrecoverable afterwards
    ///     because the second file IS the computed leaf from then on, so adoption never runs again. Only
    ///     <see cref="SandboxException.IsDefiniteMissingPath"/> means "nothing there"; anything else defers
    ///     the whole flush, which costs one repeated pass. This follows the discrimination established at
    ///     <c>SandboxClient.Files.cs</c> and in the daemon's session adapter.
    ///     </para>
    ///     <para>
    ///     A move that does not take is a different matter and is still absorbed: reporting the stale leaf
    ///     rather than the new one keeps the writer appending to the file that is there, which is always
    ///     preferable to starting a second one.
    ///     </para>
    ///     <para>
    ///     <b>The downstream guards are not enough on their own; the name needs a shape check here.</b> A
    ///     redirected <c>.conversations</c> makes this listing return attacker-chosen entries, and one of
    ///     them can be adopted as the leaf. Most of that is genuinely contained downstream: the adopted stem
    ///     must satisfy <see cref="IsThisConversation"/>, it reaches the filesystem as the guarded source
    ///     operand of <see cref="MoveScript"/>, and every write that follows is guarded at its own call
    ///     site. What is NOT contained downstream is a name that is not a single path component. An earlier
    ///     revision of this note claimed a directory entry name cannot contain a separator and concluded the
    ///     exposure was availability rather than disclosure; that was wrong, and wrong in the direction that
    ///     matters. This code does not read <c>readdir</c> — it reads a <c>name</c> field out of the
    ///     gateway's JSON, and nothing between the two enforces the invariant. A separator in that name
    ///     walks straight out of the transcript directory past an ancestor check that has nothing to refuse,
    ///     because <c>..</c> is a real directory. See <see cref="IsAddressableName"/>, which is the check
    ///     that closes it.
    ///     </para>
    /// </remarks>
    private async Task<(bool Listed, string? Leaf)> AdoptExistingLeafAsync(
        string sessionId,
        string leaf,
        CancellationToken ct
    )
    {
        IReadOnlyList<SandboxDirectoryEntry> entries;
        try
        {
            entries = await _fileBrowser
                .ListWorkspaceDirectoryAsync(sessionId, TranscriptDirectory, ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.IsDefiniteMissingPath)
        {
            // The ordinary first flush of a workspace that has never held a transcript.
            _logger.LogDebug(ex, "No {Directory} yet for thread {ThreadId}", TranscriptDirectory, ThreadId);
            return (true, null);
        }
        catch (SandboxException ex)
        {
            _logger.LogDebug(ex, "Listing {Directory} for thread {ThreadId} failed", TranscriptDirectory, ThreadId);
            return (false, null);
        }

        var stale = entries
            .Where(entry => entry.Type == SandboxEntryType.File && IsAddressableName(entry))
            .Select(entry => entry.Name)
            .Where(name => name.EndsWith(TranscriptExtension, StringComparison.Ordinal))
            .Select(name => name[..^TranscriptExtension.Length])
            .FirstOrDefault(stem => IsThisConversation(stem) && !string.Equals(stem, leaf, StringComparison.Ordinal));

        // The flag means "there is a sub-agent directory a later retitle has to move", which until now
        // only a write in THIS process could set. A restart forgot it, so the first retitle after one
        // moved the file and left the directory orphaned under the old name — the same defect as the file
        // itself, one level down. The listing is the answer for both.
        var subject = stale ?? leaf;
        _agentsDirectoryTouched =
            _agentsDirectoryTouched
            || entries.Any(entry =>
                entry.Type == SandboxEntryType.Directory
                && IsAddressableName(entry)
                && string.Equals(entry.Name, subject + AgentsDirectorySuffix, StringComparison.Ordinal)
            );

        // Deliberately no equivalent for the sidecar directory (#254): a retitle never moves it, so there
        // is no flag for a cold start to recover. Its references name the leaf they were written under
        // and keep resolving from TranscriptDirectory wherever the file goes. See MoveLeafAsync.
        if (stale is null)
        {
            return (true, null);
        }

        _transcriptExists = true;
        _logger.LogInformation(
            "Adopting the transcript of thread {ThreadId} left under {Stale} as {Leaf}",
            ThreadId,
            stale,
            leaf
        );

        return (
            true,
            await MoveLeafAsync(sessionId, stale, leaf, _agentsDirectoryTouched, ct).ConfigureAwait(false)
                ? leaf
                : stale
        );
    }

    /// <summary>
    ///     Whether a file stem is this conversation's own — i.e. carries its short id in the position
    ///     <see cref="WorkspaceTranscriptLine.MainFileLeaf"/> puts it, either as the whole stem (the title
    ///     slugged to nothing) or after the separator.
    /// </summary>
    private bool IsThisConversation(string stem) =>
        string.Equals(stem, _shortThreadId, StringComparison.Ordinal)
        || stem.EndsWith($"-{_shortThreadId}", StringComparison.Ordinal);

    /// <summary>
    ///     Whether a listed entry's name may be used to BUILD a path. Both halves are about addressability:
    ///     a lossy name cannot round-trip to the bytes on disk, and a name that is not a single path
    ///     component is not the thing the caller thinks it is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>The adopted leaf is the one path component in this type that the writer does not compute.</b>
    ///     Every other segment — the transcript directory, the temp name, the <c>.gitignore</c>, the
    ///     title-derived slug — is minted here. An adopted stem arrives as a <c>name</c> field in the
    ///     gateway's JSON, and <see cref="SandboxDirectoryEntry.Name"/> documents itself as a non-recursive
    ///     name "excluding <c>.</c> and <c>..</c>" WITHOUT enforcing it:
    ///     <c>SandboxClient.ListDirectoryEntriesAsync</c> checks only that a name is present and states that
    ///     per-entry validation is left to each caller's projection. This is that projection.
    ///     </para>
    ///     <para>
    ///     A name carrying <c>/</c> would defeat every other guard here, and not narrowly. The ancestor walk
    ///     tests each component with <c>[ -L ]</c>, and <c>..</c> is a real directory rather than a symlink,
    ///     so the walk passes; the path then reaches <c>cat &gt;&gt; "$3"</c> inside the container, which no
    ///     gateway path validation mediates because it is argv to a shell, not a file API call. The
    ///     unredacted transcript would land outside <c>.conversations</c> — outside the <c>.gitignore</c>
    ///     that is the whole containment mechanism — at exit code 0, taking the <c>_agents</c> directory
    ///     with it. A NUL is rejected on the same footing: it truncates the path at the exec boundary, so
    ///     the name this code reasons about and the name the kernel opens are different names.
    ///     </para>
    ///     <para>
    ///     <c>FileBrowserController</c> lists the same way at five call sites. Four of them compare a name
    ///     for EQUALITY against one they already hold, so a traversing name there never matches anything.
    ///     The fifth does not: it projects every entry into a DTO for DISPLAY. That is a different risk
    ///     class — a name that is rendered rather than one that is spliced into a path — and it is not what
    ///     this predicate is for. What matters here is that this is the only consumer that ADDRESSES the
    ///     filesystem with a name it did not compute, which is why the check belongs in this projection
    ///     rather than in the browser.
    ///     </para>
    /// </remarks>
    private static bool IsAddressableName(SandboxDirectoryEntry entry) =>
        !entry.NameLossy
        && entry.Name.Length > 0
        && entry.Name.AsSpan().IndexOfAny('/', '\0') < 0
        && !string.Equals(entry.Name, ".", StringComparison.Ordinal)
        && !string.Equals(entry.Name, "..", StringComparison.Ordinal);

    /// <summary>
    ///     Renames one transcript file and, when asked, its sibling sub-agent directory. The sidecar
    ///     directory is deliberately NOT renamed — see the remarks.
    /// </summary>
    /// <returns>Whether the FILE moved — the only condition under which the new leaf may be adopted.</returns>
    /// <remarks>
    ///     <para>
    ///     <b><c>{leaf}</c><see cref="BlobsDirectorySuffix"/> stays at the name it was written under,
    ///     permanently.</b> A <c>message_json_ref</c> is stamped with the leaf in force at WRITE time
    ///     (<see cref="ExternalizeIfOversizedAsync"/>) and is anchored at <see cref="TranscriptDirectory"/>,
    ///     and nothing ever rewrites a line once it is appended. So renaming the sidecar directory
    ///     invalidates every reference already in the file — a retitle that hollows out the record rather
    ///     than merely relocating it. Leaving the directory alone is what keeps those references true, and
    ///     it is the whole reason the reference embeds a leaf instead of a fixed directory name.
    ///     </para>
    ///     <para>
    ///     What that costs is a directory under a name the conversation no longer has, once per retitle
    ///     that externalised anything. That is litter and nothing worse: it lives inside
    ///     <see cref="TranscriptDirectory"/>, which the containment <c>.gitignore</c> covers whole, and it
    ///     holds payloads that are still addressed by the references that named them. The sub-agent
    ///     directory moves precisely because it carries no such stamped-in references — the file's rows
    ///     are found by walking the directory, not by a path written into a line.
    ///     </para>
    ///     <para>
    ///     Oversized writes AFTER a retitle need no repair step: the reference they mint carries the new
    ///     leaf, and the PUT that follows creates <c>{newLeaf}</c><see cref="BlobsDirectorySuffix"/>
    ///     through the SDK's one-shot parent-directory self-heal
    ///     (<c>SandboxSessionRegistry.WriteWorkspaceFileBytesAsync</c>). The two directories simply coexist,
    ///     each addressed by the references written while its leaf was current.
    ///     </para>
    /// </remarks>
    private async Task<bool> MoveLeafAsync(
        string sessionId,
        string from,
        string to,
        bool moveAgents,
        CancellationToken ct
    )
    {
        if (
            !await TryMoveAsync(
                    sessionId,
                    $"{TranscriptDirectory}/{from}{TranscriptExtension}",
                    $"{TranscriptDirectory}/{to}{TranscriptExtension}",
                    ct
                )
                .ConfigureAwait(false)
        )
        {
            return false;
        }

        if (
            moveAgents
            && !await TryMoveAsync(
                    sessionId,
                    $"{TranscriptDirectory}/{from}{AgentsDirectorySuffix}",
                    $"{TranscriptDirectory}/{to}{AgentsDirectorySuffix}",
                    ct
                )
                .ConfigureAwait(false)
        )
        {
            DropSubAgentState();
        }

        return true;
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
    ///     starving its tail, and a truncated pass reports <c>progressing</c> so another flush follows.
    ///     </para>
    ///     <para>
    ///     <b>The continuation is sized by COVERAGE, not by a retry budget.</b> A capped pass has to keep
    ///     asking — "this slice wrote nothing" says nothing about the descendants OUTSIDE it, and the one
    ///     that changed is exactly as likely to be out there — but asking under the caller's FAILURE budget
    ///     caps the roster a single trigger can ever reach at (budget + 1) x cap, silently truncating
    ///     anything larger. So the sweep tracks which descendants it has COVERED since the last external
    ///     trigger and keeps asking only while some remain.
    ///     </para>
    ///     <para>
    ///     <b>A descendant counts as covered only once it has actually been processed</b> — appended, or
    ///     read successfully and found to have nothing new. A descendant whose read or splice FAILED is
    ///     left uncovered, so a later pass retries it. Marking it covered anyway loses it outright: with a
    ///     roster larger than the cap, slice 1 could fail on descendant A and report <c>deferred</c>, the
    ///     caller's retry could run slice 2, slice 2 could succeed, and A — already marked — would leave
    ///     the sweep complete with A never mirrored and no further pass owed.
    ///     </para>
    ///     <para>
    ///     <b>Termination.</b> <c>progressing</c> means only "unfailed ground is still uncovered": it
    ///     excludes both the covered set and the descendants that failed in THIS pass, so a failure can
    ///     never by itself keep the chain alive. The two ways a pass can end therefore both make progress
    ///     against a finite bound:
    ///     <list type="bullet">
    ///     <item><description>
    ///     If a pass fails on any descendant it reports <c>deferred</c>, and the caller charges that to its
    ///     failure budget — at most <c>MaxDeferredRetries</c> of those exist per trigger.
    ///     </description></item>
    ///     <item><description>
    ///     If a pass fails on none, every descendant in its window becomes covered. The window is
    ///     <c>take</c> CONSECUTIVE slots and the cursor advances by exactly <c>take</c>, so successive
    ///     windows tile the ring: any ceil(count / take) consecutive passes visit every descendant. So a
    ///     run of failure-free passes covers the whole roster within ceil(count / take) passes, after which
    ///     nothing is uncovered and <c>progressing</c> is false.
    ///     </description></item>
    ///     </list>
    ///     A <c>progressing</c> pass costs no budget, but it cannot repeat indefinitely: within every
    ///     ceil(count / take) passes the ring is fully visited, so either the roster becomes covered
    ///     (chain ends) or a descendant fails again (budget charged). The chain is therefore bounded by
    ///     (MaxDeferredRetries + 2) x ceil(count / take) passes, and a PERMANENTLY failing descendant ends
    ///     it by exhausting the failure budget rather than by looping.
    ///     </para>
    /// </remarks>
    private async Task<(bool Wrote, bool Deferred, bool Progressing)> FlushSubAgentsAsync(
        string sessionId,
        string leaf,
        CancellationToken ct
    )
    {
        if (Interlocked.Exchange(ref _sawSubAgentActivity, 0) == 1)
        {
            _descendants.NoteAgentActivity(ThreadId);
        }

        if (Interlocked.Exchange(ref _sweepRestartRequested, 0) == 1)
        {
            _sweptAgents.Clear();
        }

        IReadOnlyList<SubAgentSummary> agents = await _descendants.GetOrScanAsync(ThreadId, ct).ConfigureAwait(false);
        if (agents.Count == 0)
        {
            return (false, false, false);
        }

        var take = Math.Min(agents.Count, _maxSubAgentFilesPerFlush);
        var wrote = false;
        var directory = $"{TranscriptDirectory}/{leaf}{AgentsDirectorySuffix}";

        // Failures of THIS pass. They stay out of _sweptAgents so a later pass retries them, and out of
        // the progressing test so they cannot keep the chain alive on their own. See the termination
        // argument above.
        var failed = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < take; i++)
        {
            var agent = agents[(_subAgentCursor + i) % agents.Count];

            var messages = await ReadStableAsync(agent.ThreadId, ct).ConfigureAwait(false);
            if (messages is null)
            {
                _ = failed.Add(agent.ThreadId);
                continue;
            }

            if (messages.Count == 0)
            {
                _ = _sweptAgents.Add(agent.ThreadId);
                continue;
            }

            var path =
                $"{directory}/{WorkspaceTranscriptLine.AgentFileLeaf(agent.Name, WorkspaceTranscriptLine.ShortId(agent.AgentId))}{TranscriptExtension}";
            var lines = WorkspaceTranscriptLine.ChainMessages(messages, agent.Name, RootParentUidFor(agent.ThreadId));

            var result = await AppendAsync(sessionId, directory, path, agent.ThreadId, lines, ct).ConfigureAwait(false);
            if (result == AppendResult.Failed)
            {
                _ = failed.Add(agent.ThreadId);
                continue;
            }

            _ = _sweptAgents.Add(agent.ThreadId);
            if (result == AppendResult.Appended)
            {
                wrote = true;
                _agentsDirectoryTouched = true;
            }
        }

        _subAgentCursor = (_subAgentCursor + take) % agents.Count;

        var progressing = agents.Any(agent =>
            !_sweptAgents.Contains(agent.ThreadId) && !failed.Contains(agent.ThreadId)
        );
        return (wrote, failed.Count > 0, progressing);
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

    /// <summary>
    ///     What a cold-start watermark probe established about the destination file.
    /// </summary>
    /// <param name="Settled">
    ///     Whether the probe reached a DEFINITE answer. False means the destination could not be inspected
    ///     — the call threw, or the probe exited non-zero for a reason that is not a definite absence — and
    ///     the flush must then append nothing at all. The alternative reading, "there is no transcript",
    ///     restarts the file from row zero and re-appends the whole persisted history onto one that already
    ///     holds it, once per cold writer, so a host restarting in a loop multiplies it indefinitely.
    /// </param>
    /// <param name="HadContent">
    ///     Whether the destination exists AND its tail window held bytes. False for a definite absence and
    ///     for an empty file, both of which are the ordinary first flush rather than something to warn
    ///     about.
    /// </param>
    /// <param name="Uid">The last intact <c>uid</c> in the tail window, when there was one.</param>
    private readonly record struct WatermarkProbe(bool Settled, bool HadContent, string? Uid)
    {
        /// <summary>The destination could not be inspected. Nothing may be appended off the back of it.</summary>
        public static WatermarkProbe Unsettled => new(Settled: false, HadContent: false, Uid: null);

        /// <summary>The destination is definitely not there — the ordinary first flush.</summary>
        public static WatermarkProbe Absent => new(Settled: true, HadContent: false, Uid: null);
    }

    /// <summary>
    ///     The message types whose payload may be externalised to a sidecar (#254).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Type, not size alone.</b> A tool result is the one payload kind whose bulk carries no
    ///     structure a reader of the transcript needs in line: it is an opaque body the tool produced,
    ///     already addressed by the row that requested it. Every other large payload - a long assistant
    ///     turn, a reasoning block, a user paste - is the conversation itself, and a reader scanning the
    ///     file expects to see it there. Externalising by size alone would hollow out exactly the lines
    ///     the transcript exists to record.
    ///     </para>
    ///     <para>
    ///     Both spellings are listed because both reach this writer: the aggregate result message and the
    ///     single-call one. The names are matched as strings for the same reason the line carries a string
    ///     - this writer never deserialises <c>message_json</c>, and gaining a type dependency here to
    ///     recognise two names would be a worse trade than restating them.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> ExternalizableMessageTypes = new(StringComparer.Ordinal)
    {
        "ToolsCallResultMessage",
        "ToolCallResultMessage",
    };

    /// <summary>
    ///     Writes an oversized tool-result payload to a sidecar file and returns the line with
    ///     <c>message_json</c> replaced by a <c>message_json_ref</c> pointing at it (#254).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Every failure inlines.</b> A transcript that records the payload is strictly better than one
    ///     that records a reference to a file that was never written, so an unsafe path, a missing leaf, or
    ///     a failed PUT all return the line untouched rather than propagating. The externalisation is an
    ///     optimisation of the record's shape; the record itself is the point.
    ///     </para>
    ///     <para>
    ///     The sidecar PUT goes through the same <see cref="IsPathSafeAsync"/> guard as every other write
    ///     in this class, so a symlink planted at the sidecar name is REFUSED - the payload inlines - and
    ///     never followed and never repaired, which is the invariant the rest of the writer holds to.
    ///     </para>
    ///     <para>
    ///     <b>The reference is stamped with the leaf in force RIGHT NOW, and that stamp is permanent.</b>
    ///     Nothing rewrites a line once it is appended, so this is the moment the sidecar's address is
    ///     fixed for good - which is why a retitle must leave <see cref="BlobsDirectorySuffix"/>
    ///     directories exactly where they are. See <see cref="MoveLeafAsync"/>. A conversation retitled
    ///     twice therefore accumulates one sidecar directory per leaf that externalised anything, each
    ///     still addressed by the lines written under it.
    ///     </para>
    /// </remarks>
    private async Task<WorkspaceTranscriptLine> ExternalizeIfOversizedAsync(
        string sessionId,
        WorkspaceTranscriptLine line,
        CancellationToken ct
    )
    {
        if (
            line.MessageJson is not { } content
            || line.MessageType is not { } messageType
            || !ExternalizableMessageTypes.Contains(messageType)
            || Encoding.UTF8.GetByteCount(content) < ToolResultSidecarThresholdBytes
        )
        {
            return line;
        }

        if (_leaf is null)
        {
            return line;
        }

        var reference = $"{_leaf}{BlobsDirectorySuffix}/{line.Uid}.json";
        var path = $"{TranscriptDirectory}/{reference}";
        if (!await IsPathSafeAsync(sessionId, path, ct).ConfigureAwait(false))
        {
            return line;
        }

        try
        {
            await _fileBrowser
                .WriteWorkspaceFileBytesAsync(sessionId, path, Encoding.UTF8.GetBytes(content), ct)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(
                ex,
                "Writing the transcript sidecar {Path} for thread {ThreadId} failed; the payload is inlined instead",
                path,
                ThreadId
            );
            return line;
        }

        return line with
        {
            MessageJson = null,
            MessageJsonRef = reference,
        };
    }

    private async Task<AppendResult> AppendAsync(
        string sessionId,
        string directory,
        string path,
        string watermarkKey,
        IReadOnlyList<WorkspaceTranscriptLine> lines,
        CancellationToken ct
    )
    {
        if (lines.Count == 0)
        {
            return AppendResult.UpToDate;
        }

        var resolved = await ResolveStartIndexAsync(sessionId, path, watermarkKey, lines, ct).ConfigureAwait(false);
        if (resolved is not { } start)
        {
            // The destination could not be INSPECTED, which is not the same as finding nothing in it.
            // Deferring costs one repeated pass; the alternative costs a duplicate of the whole history.
            // This returns before anything is staged, so the flush leaves no bytes behind either.
            return AppendResult.Failed;
        }

        if (start >= lines.Count)
        {
            return AppendResult.UpToDate;
        }

        // Containment BEFORE the first transcript byte, enforced HERE because this is the one path every
        // byte of every file passes through — the main transcript and each sub-agent file alike. See
        // EnsureContainedAsync. Below this line the workspace is written to; the staged payload lands
        // inside the very directory the ignore file covers, so it is a transcript byte like any other.
        if (!await EnsureContainedAsync(sessionId, ct).ConfigureAwait(false))
        {
            return AppendResult.Failed;
        }

        var payload = new StringBuilder();
        for (var i = start; i < lines.Count; i++)
        {
            var line = await ExternalizeIfOversizedAsync(sessionId, lines[i], ct).ConfigureAwait(false);
            _ = payload.Append(WorkspaceTranscriptLine.Serialize(line)).Append('\n');
        }

        // The staging path is guarded for the same reason the destination is, and it cannot be guarded by
        // AppendScript: the payload is PUT here BEFORE any script runs. Its name is no less guessable than
        // the transcript's — a compile-time directory plus a hash of the thread id — and the bytes it
        // carries are the same unredacted rows.
        //
        // Guarded before EVERY staging PUT, and deliberately NOT cached for the flush. One flush stages
        // the main transcript and each descendant through this ONE path, and between two consecutive PUTs
        // sits a whole AppendScript shell execution — a gateway round trip, not a millisecond. A cached
        // answer would let a link planted in that window be followed by every PUT after it, once per
        // descendant. The rationale for caching was that the path does not vary within a flush; the path
        // not varying says nothing about the FILESYSTEM AT that path not varying, which is the only thing
        // this guard reads.
        if (!await IsPathSafeAsync(sessionId, _tempPath, ct).ConfigureAwait(false))
        {
            return AppendResult.Failed;
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
            if (spliced is { ExitCode: AliasedPathExitCode })
            {
                // Reached when the alias appears in the window between IsPathSafeAsync and this splice, or
                // on the destination, which no earlier check in this method looks at.
                LogAliasedRefusal(path);
                return AppendResult.Failed;
            }

            _logger.LogWarning(
                "Transcript splice into {Path} failed with exit {ExitCode}: {Error}",
                path,
                spliced?.ExitCode ?? -1,
                spliced?.StandardError ?? "(no result)"
            );
            return AppendResult.Failed;
        }

        _watermarks[watermarkKey] = lines[^1].Uid;
        _transcriptExists = true;
        return AppendResult.Appended;
    }

    /// <summary>
    ///     Step 5. Reports the index of the first line that still needs appending — the watermark's
    ///     successor when the watermark is known and present, and otherwise zero. Null means the
    ///     destination could not be INSPECTED and the caller must append nothing at all.
    /// </summary>
    /// <remarks>
    ///     The fallback, once the destination HAS been inspected, is ALWAYS "append everything". Every other
    ///     candidate (append nothing, append the tail, guess by timestamp) trades a duplicate for a
    ///     permanently missing record, and a duplicate is recoverable by any reader that groups on
    ///     <c>uid</c>. An UNINSPECTED destination is the one case that is not a choice between those two:
    ///     see <see cref="WatermarkProbe"/>.
    /// </remarks>
    private async Task<int?> ResolveStartIndexAsync(
        string sessionId,
        string path,
        string watermarkKey,
        IReadOnlyList<WorkspaceTranscriptLine> lines,
        CancellationToken ct
    )
    {
        if (!_watermarks.TryGetValue(watermarkKey, out var watermark))
        {
            var probe = await RecoverWatermarkAsync(sessionId, path, ct).ConfigureAwait(false);
            if (!probe.Settled)
            {
                return null;
            }

            _transcriptExists = _transcriptExists || probe.HadContent;
            if (probe.Uid is null)
            {
                if (probe.HadContent)
                {
                    WarnFullAppend(path, "its tail held no readable record");
                }

                return 0;
            }

            watermark = probe.Uid;
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
    ///     per conversation to learn eight characters is not a micro-optimisation to skip. What the probe
    ///     must never do is answer an indeterminate failure the way it answers a genuine absence; see
    ///     <see cref="WatermarkProbe"/> and <see cref="WatermarkProbeScript"/>. Its output is bounded per
    ///     line, so an individual multi-megabyte row cannot make this call itself unanswerable — see
    ///     <see cref="WatermarkTailChars"/>.
    /// </remarks>
    private async Task<WatermarkProbe> RecoverWatermarkAsync(string sessionId, string path, CancellationToken ct)
    {
        var tailed = await RunAsync(
                sessionId,
                [
                    "sh",
                    "-c",
                    WatermarkProbeScript,
                    "sh",
                    path,
                    WatermarkTailLines.ToString(CultureInfo.InvariantCulture),
                    WatermarkTailChars.ToString(CultureInfo.InvariantCulture),
                ],
                ct
            )
            .ConfigureAwait(false);

        if (tailed is null)
        {
            // RunAsync answers null for a thrown SandboxException — a transport or gateway fault, which
            // says nothing whatsoever about whether the file is there.
            return WatermarkProbe.Unsettled;
        }

        if (tailed.ExitCode == WatermarkMissingExitCode)
        {
            return WatermarkProbe.Absent;
        }

        if (tailed.ExitCode is not (0 or WatermarkPartialExitCode))
        {
            if (tailed.ExitCode == AliasedPathExitCode)
            {
                // Unsettled, like every other refusal here: an aliased destination is one this writer will
                // not touch, and reporting Absent would send the caller off to append the whole history
                // into it.
                LogAliasedRefusal(path);
                return WatermarkProbe.Unsettled;
            }

            _logger.LogWarning(
                "Transcript watermark probe of {Path} failed with exit {ExitCode}: {Error}",
                path,
                tailed.ExitCode,
                tailed.StandardError
            );
            return WatermarkProbe.Unsettled;
        }

        var window = tailed.StandardOutput.Split('\n');

        var last = window.Length - 1;
        while (last >= 0 && window[last].Trim().Length == 0)
        {
            last--;
        }

        if (tailed.ExitCode == WatermarkPartialExitCode)
        {
            // The file ends mid-record, so the window's last line is the torn half of a row that was never
            // fully written. Adopting its uid would silently skip the row it belongs to for good.
            last--;
        }

        for (var i = last; i >= 0; i--)
        {
            if (TryReadUid(window[i]) is { } uid)
            {
                return new WatermarkProbe(Settled: true, HadContent: true, Uid: uid);
            }
        }

        // A readable window with nothing usable in it is still a DEFINITE answer: the file is there and
        // holds no adoptable record — empty, or torn across its whole window. Only the second of those is
        // worth a warning, which is what HadContent distinguishes.
        return new WatermarkProbe(
            Settled: true,
            HadContent: !string.IsNullOrWhiteSpace(tailed.StandardOutput),
            Uid: null
        );
    }

    /// <summary>
    ///     Reads the <c>uid</c> out of one windowed line, which by construction may be only the LEADING
    ///     <see cref="WatermarkTailChars"/> characters of the real row.
    /// </summary>
    /// <remarks>
    ///     Hence <c>isFinalBlock: false</c> rather than <see cref="JsonDocument"/>: it tells the reader the
    ///     buffer is a PREFIX, so a token cut off at the end means "no more data yet" — <c>Read</c> simply
    ///     returns false — instead of a syntax error. A whole intact line parses through the same path
    ///     unchanged, since a complete object is also a valid prefix of itself.
    /// </remarks>
    private static string? TryReadUid(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(trimmed), isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUid = reader.ValueTextEquals(UidPropertyName);
                if (!reader.Read())
                {
                    return null;
                }

                if (isUid)
                {
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                // TrySkip, not Skip: Skip THROWS on a value the prefix does not finish, which is the
                // ordinary case here rather than an error.
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                {
                    return null;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            // A line torn BEFORE its uid, or one that was never JSON. Expected — it is the reason the
            // probe reads a window instead of one line.
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
    ///     Writes <c>.conversations/.gitignore</c> containing <c>*</c>, once per conversation. Reports
    ///     whether the file is now known to be present.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     A workspace is frequently a git checkout and an agent frequently runs <c>git add -A</c>. Without
    ///     this, the first such commit publishes this conversation's and its siblings' unredacted reasoning
    ///     off-machine, irrecoverably. It is written BEFORE the first transcript byte and skipped entirely
    ///     for a conversation that will not produce a transcript, so no directory is left behind — see
    ///     <see cref="EnsureContainedAsync"/>, which owns that condition and what a <c>false</c> here costs.
    ///     </para>
    ///     <para>
    ///     The path is guarded first, and a redirected one fails containment outright. This write is the
    ///     one that DESTROYS rather than leaks: it is a PUT of <c>*</c>, so through a symlink it replaces
    ///     the target's entire contents with a single character. Refusing also covers every transcript
    ///     byte behind it at no extra cost, because the ignore path shares all its ancestors with them —
    ///     a redirected <c>.conversations</c> is caught here, before anything is staged.
    ///     </para>
    /// </remarks>
    private async Task<bool> EnsureGitignoreAsync(string sessionId, CancellationToken ct)
    {
        if (_gitignoreWritten)
        {
            return true;
        }

        var path = $"{TranscriptDirectory}/.{GitignoreName}";
        if (!await IsPathSafeAsync(sessionId, path, ct).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await _fileBrowser
                .WriteWorkspaceFileBytesAsync(sessionId, path, Encoding.UTF8.GetBytes("*\n"), ct)
                .ConfigureAwait(false);
            _gitignoreWritten = true;
            return true;
        }
        catch (SandboxException ex)
        {
            // Retried on the next flush, and the flush reports Deferred so there IS a next one. The rows
            // themselves stay written: refusing to keep them because their ignore file failed would be the
            // wrong trade, and the watermark is what guarantees the retry is a no-op for them.
            _logger.LogWarning(ex, "Writing the transcript .gitignore for thread {ThreadId} failed", ThreadId);
            return false;
        }
    }

    // -------- Gateway helpers --------

    /// <summary>
    ///     Reports whether <paramref name="path"/> and every directory on the way to it are ordinary,
    ///     un-redirected entries. <b>False means write nothing there</b> — it covers "this is a symlink"
    ///     and "this could not be checked" alike, and both are reasons to leave the workspace untouched.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Only the three writes that reach the workspace WITHOUT a shell need this — the staged payload,
    ///     the <c>.gitignore</c>, and the oversized tool-result sidecar (#254). Everything the shell writes
    ///     carries its guard inside the same script it writes with, which is both cheaper (no extra round
    ///     trip) and tighter (no gap between the check and the write). See <see cref="UnsafePathExitCode"/>
    ///     for why the answer is refusal rather than repair.
    ///     </para>
    ///     <para>
    ///     This is one of seven filesystem entry points, and the seven are enumerated ONCE, on the class.
    ///     Keep them there rather than restating them here: two copies of a coverage table drift, and a
    ///     drifted coverage table is worse than none, because the reason to enumerate is to be able to trust
    ///     the count. Anything new that touches <c>_fileBrowser</c> belongs in that list.
    ///     </para>
    /// </remarks>
    private async Task<bool> IsPathSafeAsync(string sessionId, string path, CancellationToken ct)
    {
        var guarded = await RunAsync(sessionId, ["sh", "-c", PathGuardScript, "sh", path], ct).ConfigureAwait(false);

        if (guarded is { ExitCode: 0 })
        {
            return true;
        }

        if (guarded is { ExitCode: UnsafePathExitCode })
        {
            _logger.LogWarning(
                "Transcript path {Path} for thread {ThreadId} is a symlink, or lies under one. Refusing to "
                    + "write through it: the bytes would land outside the directory the .gitignore covers. "
                    + "The link is left exactly as it is — removing it would destroy whatever it points at.",
                path,
                ThreadId
            );
            return false;
        }

        if (guarded is { ExitCode: AliasedPathExitCode })
        {
            LogAliasedRefusal(path);
            return false;
        }

        _logger.LogWarning(
            "Transcript path guard for {Path} failed with exit {ExitCode}: {Error}",
            path,
            guarded?.ExitCode ?? -1,
            guarded?.StandardError ?? "(no result)"
        );
        return false;
    }

    /// <summary>
    ///     The one message for every alias refusal, wherever it is detected. Shared rather than repeated
    ///     because this is the failure an operator will be reading BACKWARDS from a transcript that quietly
    ///     stopped growing, and the fix — remove the extra name — is not one anybody guesses from
    ///     "exit 46". See <see cref="AliasedPathExitCode"/>.
    /// </summary>
    private void LogAliasedRefusal(string path)
    {
        _logger.LogWarning(
            "Transcript path {Path} for thread {ThreadId} is not a regular file with exactly one name — "
                + "either something else in the workspace is hard-linked to it, or it is a directory. "
                + "Refusing to use it: appending would write the unredacted transcript into whatever else "
                + "shares that inode, which the .conversations/.gitignore does not cover. Nothing is "
                + "unlinked, because a hard link is symmetric and this cannot tell our file from theirs. "
                + "This conversation's mirror stays stopped until the extra link is removed.",
            path,
            ThreadId
        );
    }

    private async Task<bool> TryMoveAsync(string sessionId, string from, string to, CancellationToken ct)
    {
        var result = await RunAsync(sessionId, ["sh", "-c", MoveScript, "sh", from, to], ct).ConfigureAwait(false);
        if (result is { ExitCode: 0 })
        {
            return true;
        }

        if (result is { ExitCode: UnsafePathExitCode or MoveTargetDirectoryExitCode })
        {
            _logger.LogWarning(
                "Refusing to rename transcript {From} to {To} for thread {ThreadId} (exit {ExitCode}): one "
                    + "of the two paths is a symlink or lies under one, or the destination resolves to a "
                    + "directory — in which case mv moves the transcript INSIDE it, out of the directory "
                    + "the .gitignore covers. Nothing is renamed and nothing is unlinked; the transcript "
                    + "keeps its old name and the next flush tries again.",
                from,
                to,
                ThreadId,
                result.ExitCode
            );
            return false;
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
        CancellationToken ct
    )
    {
        try
        {
            var result = await _fileBrowser
                .ExecuteWorkspaceCommandAsync(sessionId, new SandboxCommand(arguments), ct)
                .ConfigureAwait(false);
            if (!result.OperationRecordReleased)
            {
                ReportUnreleasedOperationRecord(sessionId);
            }

            return result;
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(ex, "Transcript command {Command} failed for thread {ThreadId}", arguments[0], ThreadId);
            return null;
        }
    }

    /// <summary>
    ///     Reports the sandbox SDK's best-effort release of a completed command's gateway operation record
    ///     having failed — once per session at Warning, then at Debug. See
    ///     <see cref="_unreleasedOperationRecordReported"/> for why the cadence matters.
    /// </summary>
    private void ReportUnreleasedOperationRecord(string sessionId)
    {
        if (_unreleasedOperationRecordReported)
        {
            _logger.LogDebug(
                "Sandbox session {SessionId} is still retaining transcript operation records for thread {ThreadId}",
                sessionId,
                ThreadId
            );
            return;
        }

        _unreleasedOperationRecordReported = true;
        _logger.LogWarning(
            "Sandbox session {SessionId} did not release a completed transcript command's operation record "
                + "for thread {ThreadId}. Retained records hold their slot in the gateway's per-session "
                + "record cap until the terminal TTL expires; if this persists, later flushes will be "
                + "refused with 503 operation_capacity_exhausted. Reported once per session.",
            sessionId,
            ThreadId
        );
    }

    // -------- Metadata --------

    /// <summary>
    ///     Extracts the persisted workspace id, accepting only a genuine string (raw or
    ///     <see cref="JsonElement"/>) exactly as the file-browser route does — a workspace binding is not a
    ///     value to coerce.
    /// </summary>
    private static string? ReadWorkspaceId(ThreadMetadata? metadata) =>
        ReadStringProperty(metadata, MultiTurnAgentPool.WorkspacePropertyKey);

    private static string? ReadTitle(ThreadMetadata? metadata) => ReadStringProperty(metadata, TitlePropertyKey);

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
