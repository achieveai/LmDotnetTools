using System.Globalization;

namespace AchieveAi.LmDotnetTools.Sandbox.Command;

/// <summary>The idempotency-aware roles a command Bash submission can play.</summary>
internal enum CommandScriptKind
{
    /// <summary>The one side-effecting submission: atomically claim, run the command, persist the manifest.</summary>
    Run,

    /// <summary>Idempotent read of an operation's current claim/manifest state.</summary>
    Probe,

    /// <summary>Idempotent read of a byte range of a captured output stream.</summary>
    Read,

    /// <summary>
    /// Reclaim a verified operation's large stream files while retaining its bounded completion marker
    /// (the manifest plus lease/created), so a later same-id call is answered from the marker and the
    /// side effect is never re-run.
    /// </summary>
    Reclaim,

    /// <summary>Bounded listing of artifact directories for stale cleanup.</summary>
    Gc,

    /// <summary>
    /// Re-validated deletion of a single stale artifact directory. The delete decision is re-made in the
    /// shell from the directory's CURRENT lease/created at delete time, never from a stale listing
    /// snapshot, so a refreshed (re-active) operation is never deleted.
    /// </summary>
    GcPurge,
}

/// <summary>A parsed command-script marker line — the deterministic classification handle for a Bash submission.</summary>
internal readonly record struct CommandScriptRequest(
    CommandScriptKind Kind,
    string OperationDirectory,
    string? Digest,
    string? Stream,
    long Offset,
    long Length,
    int Max,
    string? Generation = null
);

/// <summary>
/// Builds the POSIX <c>sh</c> scripts the SDK submits to the gateway Bash tool for each phase of a
/// recoverable command execution, and parses the marker line that identifies a script's role.
/// </summary>
/// <remarks>
/// <para>
/// Every script begins with a <c>#LMSBX 1 …</c> marker comment. It is inert to a real shell (a
/// comment), but it deterministically classifies the submission — which is what lets a scripted test
/// double (and a human reader) tell the single side-effecting RUN submission apart from the
/// idempotent PROBE/READ/RECLAIM/GC/GCPURGE calls without parsing shell semantics.
/// </para>
/// <para>
/// Only RUN mutates state (claim + run + manifest). PROBE/READ are idempotent reads; RECLAIM and
/// GCPURGE only ever delete already-terminal artifacts, so re-issuing any of them during recovery
/// never re-runs the command. Injected values are safe by construction: the operation directory,
/// digest, and stream name are all POSIX single-quoted (<see cref="PosixArgv"/>) before embedding, and
/// every stale-cleanup directory name is additionally validated to be exact fixed-length lowercase hex
/// before it is used at all.
/// </para>
/// <para>
/// Portable, self-contained primitives only: file hashing reads content on stdin and tries
/// <c>sha256sum</c> then falls back to <c>shasum -a 256</c> (so the same script works on a pinned
/// Windows Git Bash, a Linux gateway, and macOS), and base64 is always fed via stdin so no coreutils
/// variant ever prints a filename alongside the digest/encoding.
/// </para>
/// </remarks>
internal static class CommandScripts
{
    private const string MarkerPrefix = "#LMSBX 1 ";
    private const string RootExpression =
        "\"${SANDBOX_WORKSPACE:-/workspace}/" + CommandArtifactLayout.ArtifactRootRelative + "\"";

    /// <summary>
    /// Portable file hash. Reads the file content on STDIN (never as an argument, so no coreutils
    /// variant appends a filename to the output), tries <c>sha256sum</c> and falls back to
    /// <c>shasum -a 256</c>, and normalizes the result to EXACTLY 64 lowercase hex characters — or the
    /// empty string when neither tool is available or the output is not a well-formed digest, which the
    /// SDK then rejects as a protocol/integrity failure rather than trusting a partial hash.
    /// </summary>
    internal const string Sha256Function =
        "lmsbx_sha256() { _h=$( (sha256sum 2>/dev/null || shasum -a 256 2>/dev/null) < \"$1\" | cut -d' ' -f1 | tr 'A-F' 'a-f' ); "
        + "case \"$_h\" in *[!0-9a-f]*) _h= ;; esac; [ ${#_h} -eq 64 ] || _h=; printf '%s' \"$_h\"; }";

    /// <summary>Emits one stream's <c>len|sha256|inline</c> triple: exact byte length, portable SHA-256, and (only when small) a base64 inline copy.</summary>
    private const string StreamJsonFunction =
        "stream_json() { f=\"$1\"; l=$(wc -c < \"$f\" 2>/dev/null || echo 0); s=$(lmsbx_sha256 \"$f\"); "
        + "if [ \"${l:-0}\" -le \"$THRESH\" ]; then i=\"\\\"$(base64 < \"$f\" 2>/dev/null | tr -d '\\n')\\\"\"; else i=null; fi; "
        + "printf '%s|%s|%s' \"$l\" \"$s\" \"$i\"; }";

    /// <summary>Emits the single MANIFEST sentinel line: the base64 of the persisted manifest, read via stdin so no filename leaks onto the line.</summary>
    private const string EmitManifestFunction =
        "emit_manifest() { b64=$(base64 < \"$MAN\" 2>/dev/null | tr -d '\\n'); printf '%s %s %s\\n' \"$SENT\" '"
        + CommandSentinel.KindManifest
        + "' \"$b64\"; }";

    /// <summary>
    /// Portable "read a small unsigned integer file, defaulting to 0" helper. A missing file, an empty
    /// file, or any non-digit content all yield <c>0</c>, so downstream numeric comparisons are always
    /// well-defined even against a torn or hostile lease/created/timestamp file.
    /// </summary>
    private const string NumFunction =
        "lmsbx_num() { _v=$(cat \"$1\" 2>/dev/null || echo 0); case \"$_v\" in ''|*[!0-9]*) _v=0 ;; esac; printf '%s' \"$_v\"; }";

    /// <summary>
    /// Portable generator of a per-acquisition unique identifier: EXACTLY 32 lowercase hex characters,
    /// used both as a GC-lock owner token and as an execution generation id. It hashes 32 bytes of
    /// <c>/dev/urandom</c> together with the shell pid and the current second, so two concurrent holders
    /// (distinct live pids) and two executions separated in time both get distinct tokens. It relies only
    /// on the coreutils the wrapper already requires (<c>sha256sum</c>/<c>shasum</c>, <c>head</c>,
    /// <c>date</c>, <c>cut</c>, <c>tr</c>) — never <c>od</c>/<c>$RANDOM</c> — and if <c>/dev/urandom</c> is
    /// unreadable the pid+second seed still yields a well-distributed value, with a final pid-derived
    /// last-ditch fallback so the result is never empty.
    /// </summary>
    internal const string UidFunction =
        "lmsbx_uid() { _u=$( (head -c 32 /dev/urandom 2>/dev/null; printf '%s' \"$$ $(date +%s)\") "
        + "| (sha256sum 2>/dev/null || shasum -a 256 2>/dev/null) | cut -d' ' -f1 | tr 'A-F' 'a-f' | cut -c1-32 ); "
        + "case \"$_u\" in *[!0-9a-f]*) _u= ;; esac; [ ${#_u} -eq 32 ] || _u=$(printf '%032d' \"$$\"); printf '%s' \"$_u\"; }";

    /// <summary>
    /// The per-operation GC-lock primitive: a sibling <c>&lt;op&gt;.gc</c> directory whose sole owner is
    /// elected by an atomic <c>mkdir</c> and then IDENTIFIED by a unique owner token
    /// (<see cref="UidFunction"><c>lmsbx_uid</c></see>) persisted in <c>&lt;op&gt;.gc/owner</c>. Deletion
    /// of an operation directory (a stale-sweep purge or an abandoned-claim self-recovery) and reclaim of
    /// output may proceed ONLY while holding this lock, and every holder both re-validates the operation's
    /// CURRENT state and re-verifies its own token (<c>gclock_owned</c>) immediately before the destructive
    /// action — so exactly one true owner ever deletes, even in the rare <c>gclock_try</c> reclaim race.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner-token fencing.</b> <c>gclock_owned</c> is true only while <c>&lt;op&gt;.gc/owner</c> still
    /// carries THIS caller's token. Every destructive action is gated on it, and <c>gclock_release</c>
    /// removes the lock ONLY when still owned — so a delayed release from an old owner whose lock was
    /// already stale-reclaimed by a successor can NEVER delete the successor's lock (the double-ownership
    /// bug this closes).
    /// </para>
    /// <para>
    /// <b>Liveness favours safety.</b> <c>gclock_is_live</c> treats a lock whose <c>owner</c> file is not
    /// yet present as LIVE — a just-<c>mkdir</c>'d lock mid-establishment (the tiny <c>mkdir</c>→token
    /// window) is respected, never reclaimed, so a contender in that window backs off instead of racing
    /// into double ownership. A lock WITH an owner token is fresh only while stamped within
    /// <see cref="CommandArtifactLayout.GcLockStaleSeconds"/>; an old stamp is a crashed holder's leftover
    /// that <c>gclock_try</c> reclaims (<c>rm -rf</c> + re-<c>mkdir</c> + re-token). The residual boundary
    /// is honest: a crash in the single-syscall <c>mkdir</c>→first-write window leaves an owner-less lock
    /// that the fast TTL will not reclaim (it is treated live forever), blocking only that one operation's
    /// maintenance — never same-id idempotency or the manifest fast path — until the artifact root is
    /// reset. Correctness never relies on the TTL: every delete is additionally gated on a fresh under-lock
    /// re-validation and the owner-token re-check.
    /// </para>
    /// </remarks>
    internal const string GcLockFunctions =
        NumFunction
        + "\n"
        + "gclock_owner_token() { cut -d' ' -f1 \"$GCL/owner\" 2>/dev/null; }\n"
        + "gclock_owner_ts() { _t=$(cut -d' ' -f2 \"$GCL/owner\" 2>/dev/null); case \"$_t\" in ''|*[!0-9]*) _t=0 ;; esac; printf '%s' \"$_t\"; }\n"
        + "gclock_owned() { [ -f \"$GCL/owner\" ] && [ \"$(gclock_owner_token)\" = \"$OWNER\" ]; }\n"
        + "gclock_is_live() { [ -d \"$GCL\" ] || return 1; [ -f \"$GCL/owner\" ] || return 0; "
        + "_lts=$(gclock_owner_ts); _now=$(date +%s); [ \"$_lts\" -gt 0 ] && [ \"$((_now - _lts))\" -le \"$GCLOCKTTL\" ]; }\n"
        + "gclock_write_owner() { printf '%s %s' \"$OWNER\" \"$(date +%s)\" > \"$GCL/owner\" 2>/dev/null; }\n"
        + "gclock_try() { "
        + "if mkdir \"$GCL\" 2>/dev/null; then gclock_write_owner; gclock_owned; return; fi; "
        + "if gclock_is_live; then return 1; fi; "
        + "rm -rf \"$GCL\" 2>/dev/null; "
        + "if mkdir \"$GCL\" 2>/dev/null; then gclock_write_owner; gclock_owned; return; fi; "
        + "return 1; }\n"
        + "gclock_release() { gclock_owned && rm -rf \"$GCL\" 2>/dev/null; return 0; }";

    /// <summary>
    /// Builds the single side-effecting wrapper. In order: fast-path an already-persisted manifest;
    /// atomically <c>mkdir</c>-claim and run (electing exactly one submitter); otherwise report PENDING
    /// so a caller behind an existing claim polls instead of re-running. The command's exact
    /// stdout/stderr are captured to files and the metadata manifest is persisted atomically, so nothing
    /// depends on the gateway's truncating <c>exec</c> output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Claim election and abandoned-claim self-recovery.</b> Electing a submitter is a single atomic
    /// <c>mkdir</c> of the operation directory: the winner runs, every other concurrent caller sees the
    /// existing claim and reports PENDING. A crashed submitter can leave an <i>abandoned</i> claim (an
    /// established-but-expired lease and no manifest); rather than block a same-id retry until the 24h
    /// sweep, the wrapper recovers it in place on that retry — but ONLY under the per-operation GC lock
    /// (<c>gclock_try</c>) and ONLY after re-validating, under that lock, that the claim is still expired
    /// and uncommitted, then it re-elects exactly one new claimant via the same atomic <c>mkdir</c>. A
    /// still-active or still-establishing (lease-less) claim is never recovered — a same-id caller there
    /// reports PENDING and polls rather than resubmitting — and claim creation itself respects a live GC
    /// lock, so a purge in progress can never be raced into a double-run. The claim loop is bounded (one
    /// recovery attempt), so it always terminates.
    /// </para>
    /// <para>
    /// <b>Umask is scoped to SDK artifacts only.</b> A restrictive <c>umask 077</c> governs the
    /// operation directory and every SDK artifact (lease/created/digest, the pre-created stdout/stderr
    /// capture files, and the manifest), but the caller's original umask is restored around the command
    /// itself so files the command creates inherit the caller's normal permissions, not the SDK's
    /// hardened ones.
    /// </para>
    /// <para>
    /// <b>The manifest is published atomically.</b> It is written to a restrictive sibling temp file and
    /// <c>mv</c>-renamed into place, so a concurrent PROBE observes either no manifest or the complete
    /// manifest — never a torn, partially-written one.
    /// </para>
    /// </remarks>
    public static string BuildRun(
        string operationDirectory,
        string digest,
        string quotedArgv,
        string normalizedWorkingDirectory,
        long executionTimeoutSeconds
    )
    {
        var runCommandLine =
            (
                normalizedWorkingDirectory.Length == 0
                    ? string.Empty
                    : "  WD=\"$WD/\"" + PosixArgv.Quote(normalizedWorkingDirectory) + "\n"
            )
            + "  if cd \"$WD\" 2>/dev/null; then ( "
            + quotedArgv
            + " ) > \"$OUT\" 2> \"$ERR\"; code=$?; else : > \"$OUT\"; printf 'sandbox: working directory not found\\n' > \"$ERR\"; code=125; fi";

        // Order matters: umask 077 is set before the claim (so the operation directory and every SDK
        // artifact is owner-only), restored to the caller's umask around the command (so files the
        // command creates get normal permissions), then reapplied for the atomic manifest publish.
        return string.Join(
            '\n',
            MarkerPrefix + "RUN op=" + operationDirectory + " digest=" + digest,
            "set -u",
            "ROOT=" + RootExpression,
            "OP=" + OpAssignment(operationDirectory),
            "GCL=\"$OP.gc\"",
            "MAN=\"$OP/manifest.json\"",
            "OUT=\"$OP/stdout\"",
            "ERR=\"$OP/stderr\"",
            "DIGEST='" + digest + "'",
            "THRESH=" + CommandArtifactLayout.InlineThresholdBytes.ToString(CultureInfo.InvariantCulture),
            "EXEC=" + executionTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            "GRACE=" + CommandArtifactLayout.LeaseGraceSeconds.ToString(CultureInfo.InvariantCulture),
            "GCLOCKTTL=" + CommandArtifactLayout.GcLockStaleSeconds.ToString(CultureInfo.InvariantCulture),
            "SENT='" + CommandSentinel.Marker + "'",
            Sha256Function,
            StreamJsonFunction,
            EmitManifestFunction,
            UidFunction,
            GcLockFunctions,
            "OWNER=$(lmsbx_uid)",
            "GEN=$(lmsbx_uid)",
            "claim_run() {",
            "  created=$(date +%s)",
            "  lease=$((created + EXEC + GRACE))",
            "  printf '%s' \"$lease\" > \"$OP/lease\"",
            "  printf '%s' \"$created\" > \"$OP/created\"",
            "  printf '%s' \"$DIGEST\" > \"$OP/digest\"",
            "  printf '%s' \"$GEN\" > \"$OP/gen\"",
            "  : > \"$OUT\"",
            "  : > \"$ERR\"",
            "  umask \"$OLDUMASK\"",
            "  WD=\"${SANDBOX_WORKSPACE:-/workspace}\"",
            runCommandLine,
            "  umask 077",
            "  so=$(stream_json \"$OUT\"); se=$(stream_json \"$ERR\")",
            "  ol=${so%%|*}; sor=${so#*|}; os=${sor%%|*}; oi=${sor#*|}",
            "  el=${se%%|*}; ser=${se#*|}; es=${ser%%|*}; ei=${ser#*|}",
            "  MTMP=\"$OP/.manifest.$$.tmp\"",
            "  printf '{\"v\":1,\"digest\":\"%s\",\"gen\":\"%s\",\"exit\":%d,\"stdout\":{\"len\":%d,\"sha256\":\"%s\",\"inline\":%s},\"stderr\":{\"len\":%d,\"sha256\":\"%s\",\"inline\":%s},\"lease\":%d,\"created\":%d}' \"$DIGEST\" \"$GEN\" \"$code\" \"$ol\" \"$os\" \"$oi\" \"$el\" \"$es\" \"$ei\" \"$lease\" \"$created\" > \"$MTMP\"",
            "  mv \"$MTMP\" \"$MAN\"",
            "  umask \"$OLDUMASK\"",
            "  emit_manifest",
            "}",
            "OLDUMASK=$(umask)",
            "umask 077",
            "mkdir -p \"$ROOT\" 2>/dev/null",
            // Bounded claim loop: at most one abandoned-claim self-recovery, then a final claim/PENDING.
            "attempt=0",
            "while [ \"$attempt\" -lt 2 ]; do",
            "  attempt=$((attempt+1))",
            "  if [ -f \"$MAN\" ]; then emit_manifest; exit 0; fi",
            // Respect an in-progress purge of THIS operation: while a live GC lock is held a purger may be
            // deleting the directory, so never create/recover a claim that races it — fall through to
            // PENDING and let a later retry proceed. A stale lock (a crashed holder) is reclaimed through
            // the ownership protocol (gclock_try then a token-checked release) — never a blind rm that
            // could delete a successor's freshly-elected lock.
            "  if [ -d \"$GCL\" ]; then",
            "    if gclock_is_live; then break; fi",
            "    if gclock_try; then gclock_release; fi",
            "  fi",
            "  if mkdir \"$OP\" 2>/dev/null; then claim_run; exit 0; fi",
            "  if [ -f \"$MAN\" ]; then emit_manifest; exit 0; fi",
            // An existing, uncommitted claim. Recover ONLY an abandoned one: an ESTABLISHED lease that has
            // EXPIRED (a lease-less mid-establishment claim, or an unexpired/active lease, is never
            // touched). The recovery deletes and re-claims under the per-operation GC lock, re-validating
            // the current state AND re-verifying our own lock token (gclock_owned) immediately before the
            // delete, so exactly one new claimant is elected and a still-active or freshly-replaced claim
            // is never destroyed even if gclock_try's stale-reclaim raced another contender.
            "  if [ -f \"$OP/lease\" ]; then",
            "    lease=$(lmsbx_num \"$OP/lease\")",
            "    now=$(date +%s)",
            "    if [ \"$lease\" -gt 0 ] && [ \"$now\" -gt \"$lease\" ]; then",
            "      if gclock_try; then",
            "        if [ ! -f \"$MAN\" ] && [ -f \"$OP/lease\" ]; then",
            "          rlease=$(lmsbx_num \"$OP/lease\")",
            "          rnow=$(date +%s)",
            "          if [ \"$rlease\" -gt 0 ] && [ \"$rnow\" -gt \"$rlease\" ] && gclock_owned; then rm -rf \"$OP\" 2>/dev/null; fi",
            "        fi",
            "        gclock_release",
            "        continue",
            "      fi",
            "    fi",
            "  fi",
            "  break",
            "done",
            "if [ -f \"$MAN\" ]; then emit_manifest; else printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindPending
                + "'; fi"
        );
    }

    /// <summary>Builds an idempotent probe of an operation's claim/manifest state.</summary>
    public static string BuildProbe(string operationDirectory) =>
        string.Join(
            '\n',
            MarkerPrefix + "PROBE op=" + operationDirectory,
            "ROOT=" + RootExpression,
            "OP=" + OpAssignment(operationDirectory),
            "MAN=\"$OP/manifest.json\"",
            "SENT='" + CommandSentinel.Marker + "'",
            EmitManifestFunction,
            "if [ -f \"$MAN\" ]; then emit_manifest; elif [ -d \"$OP\" ]; then printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindPending
                + "'; else printf '%s %s\\n' \"$SENT\" '"
                + CommandSentinel.KindNone
                + "'; fi"
        );

    /// <summary>Builds an idempotent base64 read of the byte range <c>[offset, offset+length)</c> of a captured stream.</summary>
    public static string BuildRead(string operationDirectory, string stream, long offset, long length) =>
        string.Join(
            '\n',
            MarkerPrefix
                + "READ op="
                + operationDirectory
                + " stream="
                + stream
                + " off="
                + offset.ToString(CultureInfo.InvariantCulture)
                + " len="
                + length.ToString(CultureInfo.InvariantCulture),
            "ROOT=" + RootExpression,
            "F=\"$ROOT/\"" + PosixArgv.Quote(operationDirectory) + "\"/\"" + PosixArgv.Quote(stream),
            "tail -c +"
                + (offset + 1).ToString(CultureInfo.InvariantCulture)
                + " \"$F\" 2>/dev/null | head -c "
                + length.ToString(CultureInfo.InvariantCulture)
                + " | base64 | tr -d '\\n'"
        );

    /// <summary>
    /// Builds a lock- and generation-safe reclaim of a verified operation's large stream files. It deletes
    /// only <c>stdout</c>/<c>stderr</c> (the unbounded artifacts), retaining the bounded completion marker —
    /// the manifest plus the lease/created/digest/gen files — so a later same-id call recovers the retained
    /// result (or is safely rejected without re-running) and the stale sweep can still reclaim the marker
    /// once it is old enough.
    /// </summary>
    /// <remarks>
    /// The delete happens ONLY while holding the per-operation GC lock and ONLY after re-reading, UNDER the
    /// lock, that the directory's CURRENT execution generation and command digest still equal
    /// <paramref name="generation"/> and <paramref name="digest"/> (the values from the manifest the SDK
    /// verified) and a manifest is still present — then re-verifying our own lock token. A delayed reclaim
    /// issued by an expired OLD execution therefore never touches a NEWER re-execution that reused the same
    /// operation directory: its generation no longer matches, so its output is left intact.
    /// </remarks>
    public static string BuildReclaim(string operationDirectory, string generation, string digest) =>
        string.Join(
            '\n',
            MarkerPrefix + "RECLAIM op=" + operationDirectory + " gen=" + generation + " digest=" + digest,
            "ROOT=" + RootExpression,
            "OP=" + OpAssignment(operationDirectory),
            "GCL=\"$OP.gc\"",
            "MAN=\"$OP/manifest.json\"",
            "SENT='" + CommandSentinel.Marker + "'",
            "GEN='" + generation + "'",
            "DIGEST='" + digest + "'",
            "GCLOCKTTL=" + CommandArtifactLayout.GcLockStaleSeconds.ToString(CultureInfo.InvariantCulture),
            UidFunction,
            GcLockFunctions,
            "OWNER=$(lmsbx_uid)",
            // Only the GC-lock winner may reclaim, and only for the SAME generation it verified; a losing
            // caller (a live purge/reclaim in flight) falls straight through without touching any output.
            "if gclock_try; then",
            "  if [ -f \"$MAN\" ] && [ -f \"$OP/gen\" ] && [ -f \"$OP/digest\" ]; then",
            "    curgen=$(cat \"$OP/gen\" 2>/dev/null)",
            "    curdig=$(cat \"$OP/digest\" 2>/dev/null)",
            "    if [ \"$curgen\" = \"$GEN\" ] && [ \"$curdig\" = \"$DIGEST\" ] && gclock_owned; then rm -f \"$OP/stdout\" \"$OP/stderr\" 2>/dev/null; fi",
            "  fi",
            "  gclock_release",
            "fi",
            "printf '%s %s\\n' \"$SENT\" '" + CommandSentinel.KindNone + "'"
        );

    /// <summary>Builds a bounded listing (at most <paramref name="max"/> entries) of artifact directories for stale cleanup.</summary>
    /// <remarks>
    /// Only a directory whose name is EXACTLY fixed-length lowercase hex (an SDK-derived operation
    /// directory) and that has an established lease AND created timestamp is listed. A foreign or hostile
    /// filesystem entry — or a claim still establishing itself — is skipped, so it can neither be offered
    /// as a cleanup candidate nor smuggle shell metacharacters onto the listing line.
    /// </remarks>
    public static string BuildGc(int max) =>
        string.Join(
            '\n',
            MarkerPrefix + "GC max=" + max.ToString(CultureInfo.InvariantCulture),
            "ROOT=" + RootExpression,
            "GC='" + CommandSentinel.GcMarker + "'",
            "n=0",
            "for d in \"$ROOT\"/*/; do",
            "  [ -d \"$d\" ] || continue",
            "  n=$((n+1))",
            "  [ \"$n\" -gt " + max.ToString(CultureInfo.InvariantCulture) + " ] && break",
            "  name=$(basename \"$d\")",
            "  case \"$name\" in *[!0-9a-f]*) continue ;; esac",
            "  [ ${#name} -eq "
                + CommandArtifactLayout.OperationDirectoryNameLength.ToString(CultureInfo.InvariantCulture)
                + " ] || continue",
            "  [ -f \"$d/lease\" ] && [ -f \"$d/created\" ] || continue",
            "  lease=$(cat \"$d/lease\" 2>/dev/null || echo 0)",
            "  created=$(cat \"$d/created\" 2>/dev/null || echo 0)",
            "  printf '%s %s %s %s\\n' \"$GC\" \"$name\" \"$lease\" \"$created\"",
            "done"
        );

    /// <summary>
    /// Builds a re-validated, GC-lock-guarded deletion of a single stale artifact directory. Deletion
    /// happens ONLY while holding the per-operation GC lock (<c>gclock_try</c>): a purger that loses the
    /// election never deletes. The eligibility decision is then re-made HERE, from the directory's
    /// current lease/created read at delete time UNDER the lock, rather than trusted from the SDK's
    /// earlier listing snapshot — and the purger re-verifies its own lock token (<c>gclock_owned</c>)
    /// immediately before the delete — so an operation that was refreshed (a re-established or extended
    /// lease) or replaced by a new active claim between the listing and this call is never deleted. The age
    /// test is STRICTLY greater than the retention window, so an operation exactly 24h old is still retained.
    /// </summary>
    public static string BuildGcPurge(string operationDirectory) =>
        string.Join(
            '\n',
            MarkerPrefix + "GCPURGE op=" + operationDirectory,
            "ROOT=" + RootExpression,
            "OP=" + OpAssignment(operationDirectory),
            "GCL=\"$OP.gc\"",
            "SENT='" + CommandSentinel.Marker + "'",
            "STALE=" + CommandArtifactLayout.StaleAgeSeconds.ToString(CultureInfo.InvariantCulture),
            "GCLOCKTTL=" + CommandArtifactLayout.GcLockStaleSeconds.ToString(CultureInfo.InvariantCulture),
            UidFunction,
            GcLockFunctions,
            "OWNER=$(lmsbx_uid)",
            // Only the GC-lock winner may revalidate and delete; a losing purger falls straight through.
            "if gclock_try; then",
            "  if [ -f \"$OP/lease\" ] && [ -f \"$OP/created\" ]; then",
            "    lease=$(lmsbx_num \"$OP/lease\")",
            "    created=$(lmsbx_num \"$OP/created\")",
            "    now=$(date +%s)",
            "    if [ \"$lease\" -gt 0 ] && [ \"$created\" -gt 0 ] && [ \"$now\" -gt \"$lease\" ] && [ \"$((now - created))\" -gt \"$STALE\" ] && gclock_owned; then rm -rf \"$OP\" 2>/dev/null; fi",
            "  fi",
            "  gclock_release",
            "fi",
            "printf '%s %s\\n' \"$SENT\" '" + CommandSentinel.KindNone + "'"
        );

    /// <summary>
    /// The operation-directory assignment fragment <c>"$ROOT/"'&lt;dir&gt;'</c>: the workspace-rooted
    /// prefix stays in double quotes so <c>$ROOT</c> expands, while the directory itself is POSIX
    /// single-quoted so no byte in it is ever interpreted by the shell (defense in depth — the value is
    /// already SDK-derived hex, but a stale-cleanup name read off the filesystem is untrusted).
    /// </summary>
    private static string OpAssignment(string operationDirectory) =>
        "\"$ROOT/\"" + PosixArgv.Quote(operationDirectory);

    /// <summary>
    /// Parses the leading marker line of a submitted command into its role and parameters. Throws
    /// <see cref="FormatException"/> for a body with no recognizable marker (a programming error at
    /// call sites that only ever pass scripts this class built, and a clear failure for a test double).
    /// </summary>
    public static CommandScriptRequest ParseRequest(string command)
    {
        var firstLine = command.Split('\n', 2)[0];
        if (!firstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Command body is missing the LMSBX marker line.");
        }

        var tokens = firstLine[MarkerPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kind = tokens[0] switch
        {
            "RUN" => CommandScriptKind.Run,
            "PROBE" => CommandScriptKind.Probe,
            "READ" => CommandScriptKind.Read,
            "RECLAIM" => CommandScriptKind.Reclaim,
            "GC" => CommandScriptKind.Gc,
            "GCPURGE" => CommandScriptKind.GcPurge,
            _ => throw new FormatException($"Unknown LMSBX command kind '{tokens[0]}'."),
        };

        string op = string.Empty,
            stream = string.Empty;
        string? digest = null;
        string? generation = null;
        long offset = 0,
            length = 0;
        var max = 0;
        foreach (var token in tokens.Skip(1))
        {
            var separator = token.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = token[..separator];
            var value = token[(separator + 1)..];
            switch (key)
            {
                case "op":
                    op = value;
                    break;
                case "digest":
                    digest = value;
                    break;
                case "gen":
                    generation = value;
                    break;
                case "stream":
                    stream = value;
                    break;
                case "off":
                    offset = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "len":
                    length = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "max":
                    max = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    break;
            }
        }

        return new CommandScriptRequest(
            kind,
            op,
            digest,
            stream.Length == 0 ? null : stream,
            offset,
            length,
            max,
            generation
        );
    }
}
