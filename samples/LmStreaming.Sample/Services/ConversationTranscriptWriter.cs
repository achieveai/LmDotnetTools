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
///     cached.</b> Every script guards its own path parameters, and the two writes that bypass the shell
///     entirely — the staged payload and the <c>.gitignore</c>, both PUT by the gateway — are guarded by
///     <see cref="IsPathSafeAsync"/> immediately before each PUT. The staging guard in particular runs per
///     APPEND rather than per flush: one flush stages the main transcript and every descendant through the
///     same path, with a full gateway round trip between consecutive PUTs. A redirected path is refused and
///     left alone, never unlinked; see <see cref="UnsafePathExitCode"/>.
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
    ///     Defines the <c>guard</c> shell function every script opens with: it walks a path and each of its
    ///     ancestor directories and fails if ANY of them is a symlink. Prepended, never interpolated into.
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
    ///     This narrows the window rather than closing it: a link planted between the guard and the write
    ///     is still followed. Closing it needs <c>O_NOFOLLOW</c>, which neither the gateway's file PUT nor
    ///     a POSIX shell redirection exposes. The residue is a race an attacker must win against a
    ///     millisecond; what it replaces is a symlink that could be planted at leisure, days ahead, and
    ///     would be followed with certainty.
    ///     </para>
    /// </remarks>
    private const string PathGuardPreamble =
        "guard() { p=\"$1\"; while [ -n \"$p\" ] && [ \"$p\" != \".\" ] && [ \"$p\" != \"/\" ]; do "
        + "if [ -L \"$p\" ]; then return 1; fi; p=$(dirname \"$p\"); done; return 0; }\n";

    /// <summary>
    ///     One guard invocation for the given positional parameter, reporting
    ///     <see cref="UnsafePathExitCode"/>. A method rather than three literals so the exit code cannot
    ///     drift between the parameters.
    /// </summary>
    private static string GuardLine(string parameter) =>
        "guard \""
        + parameter
        + "\" || exit "
        + UnsafePathExitCode.ToString(CultureInfo.InvariantCulture)
        + "\n";

    /// <summary>
    ///     The guard on its own, for the two writes that reach the workspace WITHOUT a shell — the staged
    ///     payload and the <c>.gitignore</c>, both of which the gateway PUTs directly. <c>$1</c> is the
    ///     path. Reads nothing, writes nothing. See <see cref="IsPathSafeAsync"/>.
    /// </summary>
    private static readonly string PathGuardScript = PathGuardPreamble + GuardLine("$1");

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
    /// </remarks>
    private static readonly string AppendScript =
        PathGuardPreamble
        + GuardLine("$1")
        + GuardLine("$2")
        + GuardLine("$3")
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
    /// </remarks>
    private static readonly string WatermarkProbeScript =
        PathGuardPreamble
        + GuardLine("$1")
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
        ).ConfigureAwait(false);

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
        var (agentsWrote, agentsDeferred, agentsProgressing) =
            await FlushSubAgentsAsync(sessionId, leaf, ct).ConfigureAwait(false);
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
    /// </remarks>
    private async Task<(bool Listed, string? Leaf)> AdoptExistingLeafAsync(
        string sessionId,
        string leaf,
        CancellationToken ct)
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
            .Where(entry => entry.Type == SandboxEntryType.File && !entry.NameLossy)
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
                && !entry.NameLossy
                && string.Equals(entry.Name, subject + AgentsDirectorySuffix, StringComparison.Ordinal));

        if (stale is null)
        {
            return (true, null);
        }

        _transcriptExists = true;
        _logger.LogInformation(
            "Adopting the transcript of thread {ThreadId} left under {Stale} as {Leaf}",
            ThreadId,
            stale,
            leaf);

        return (
            true,
            await MoveLeafAsync(sessionId, stale, leaf, _agentsDirectoryTouched, ct).ConfigureAwait(false)
                ? leaf
                : stale);
    }

    /// <summary>
    ///     Whether a file stem is this conversation's own — i.e. carries its short id in the position
    ///     <see cref="WorkspaceTranscriptLine.MainFileLeaf"/> puts it, either as the whole stem (the title
    ///     slugged to nothing) or after the separator.
    /// </summary>
    private bool IsThisConversation(string stem) =>
        string.Equals(stem, _shortThreadId, StringComparison.Ordinal)
        || stem.EndsWith($"-{_shortThreadId}", StringComparison.Ordinal);

    /// <summary>Renames one transcript file and, when asked, its sibling sub-agent directory.</summary>
    /// <returns>Whether the FILE moved — the only condition under which the new leaf may be adopted.</returns>
    private async Task<bool> MoveLeafAsync(
        string sessionId,
        string from,
        string to,
        bool moveAgents,
        CancellationToken ct)
    {
        if (!await TryMoveAsync(
                sessionId,
                $"{TranscriptDirectory}/{from}{TranscriptExtension}",
                $"{TranscriptDirectory}/{to}{TranscriptExtension}",
                ct
            ).ConfigureAwait(false))
        {
            return false;
        }

        if (moveAgents
            && !await TryMoveAsync(
                sessionId,
                $"{TranscriptDirectory}/{from}{AgentsDirectorySuffix}",
                $"{TranscriptDirectory}/{to}{AgentsDirectorySuffix}",
                ct
            ).ConfigureAwait(false))
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
        CancellationToken ct)
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

            var path = $"{directory}/{WorkspaceTranscriptLine.AgentFileLeaf(agent.Name, WorkspaceTranscriptLine.ShortId(agent.AgentId))}{TranscriptExtension}";
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

        var resolved = await ResolveStartIndexAsync(sessionId, path, watermarkKey, lines, ct)
            .ConfigureAwait(false);
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
            _ = payload.Append(WorkspaceTranscriptLine.Serialize(lines[i])).Append('\n');
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
        CancellationToken ct)
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
    private async Task<WatermarkProbe> RecoverWatermarkAsync(
        string sessionId,
        string path,
        CancellationToken ct)
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
        ).ConfigureAwait(false);

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
    ///     Only the two writes that reach the workspace WITHOUT a shell need this — the staged payload and
    ///     the <c>.gitignore</c>. Everything the shell writes carries its guard inside the same script it
    ///     writes with, which is both cheaper (no extra round trip) and tighter (no gap between the check
    ///     and the write). See <see cref="UnsafePathExitCode"/> for why the answer is refusal rather than
    ///     repair.
    /// </remarks>
    private async Task<bool> IsPathSafeAsync(string sessionId, string path, CancellationToken ct)
    {
        var guarded = await RunAsync(sessionId, ["sh", "-c", PathGuardScript, "sh", path], ct)
            .ConfigureAwait(false);

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

        _logger.LogWarning(
            "Transcript path guard for {Path} failed with exit {ExitCode}: {Error}",
            path,
            guarded?.ExitCode ?? -1,
            guarded?.StandardError ?? "(no result)"
        );
        return false;
    }

    private async Task<bool> TryMoveAsync(string sessionId, string from, string to, CancellationToken ct)
    {
        var result = await RunAsync(sessionId, ["sh", "-c", MoveScript, "sh", from, to], ct)
            .ConfigureAwait(false);
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
