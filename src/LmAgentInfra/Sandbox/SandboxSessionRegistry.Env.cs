using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>Outcome of <see cref="SandboxSessionRegistry.EnsureSessionEnvAsync"/>.</summary>
public enum SandboxEnvApplyResult
{
    /// <summary>The desired map already matched what the registry last applied; nothing was sent.</summary>
    Unchanged,

    /// <summary>The gateway accepted a PATCH bringing the session's env to the desired map.</summary>
    Patched,

    /// <summary>The gateway reported the session no longer exists (<c>session_not_found</c>).</summary>
    SessionGone,

    /// <summary>
    /// The gateway has no session-env route at all (predates v0.1.11). Per-sandbox env is disabled for
    /// the rest of this process's lifetime once this is observed.
    /// </summary>
    Unsupported,
}

/// <summary>
/// Per-session live-env state: keeps each session's env equal to a caller's desired map by diffing
/// against the last map the registry knows was applied, and PATCHing only the difference (spec Section
/// 8 — "per-sandbox env"). Complements the create-time seeding in <c>CreateSessionAsync</c>
/// (<see cref="SandboxSessionRegistry.SeedSessionEnvState"/>), which is this cache's normal entry point;
/// <see cref="SandboxSessionRegistry.EnsureSessionEnvAsync"/> falls back to a GET only when no seed
/// exists (e.g. the process restarted and the session outlived it).
/// </summary>
public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// A session's env cache entry: the map the registry believes is CURRENTLY applied (either seeded
    /// from create, read via GET, or returned by the most recent successful PATCH), plus the id of the
    /// thread that last activated/touched it — so a caller resuming a conversation can tell whether it
    /// was the last one to shape this session's env.
    /// </summary>
    /// <param name="LastApplied">The env map the registry believes the session currently carries.</param>
    /// <param name="LastActivatedThreadId">
    /// The thread that most recently activated this session, or <see langword="null"/> when the entry was
    /// seeded at create and no thread has reconciled it since.
    /// </param>
    /// <param name="Confirmed">
    /// Whether <paramref name="LastApplied"/> came from the GATEWAY (a GET, or a PATCH response) rather
    /// than from what this process merely ASKED for on create. An unconfirmed entry is treated as no
    /// entry by <see cref="EnsureSessionEnvAsync"/>, which is what makes the "older gateway" probe
    /// actually run: a pre-v0.1.11 gateway silently ignores <c>env</c> on create, so seeding the request
    /// map as though it were applied made the first reconcile diff to zero and return without ever
    /// touching the network — leaving <see cref="SessionEnvSupported"/> reporting true forever on a
    /// gateway that cannot do env at all.
    /// </param>
    private sealed record SessionEnvState(
        IReadOnlyDictionary<string, string> LastApplied,
        string? LastActivatedThreadId,
        bool Confirmed
    );

    /// <summary>
    /// Per-session env cache. Every read-diff-PATCH-write sequence for one session id runs under that
    /// session's entry in <see cref="_sessionEnvLocks"/>, so an entry is never written back from a
    /// snapshot another caller has already superseded.
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionEnvState> _sessionEnv = new(StringComparer.Ordinal);

    /// <summary>
    /// One mutex per session id, serialising <see cref="EnsureSessionEnvAsync"/> for that session.
    /// <para>
    /// This is not defensive tidiness. The reconcile reads the cache, diffs, awaits a PATCH, then writes
    /// the result back; two threads activating the SAME session concurrently (two conversations sharing a
    /// workspace, or a turn overlapping a workspace edit) would interleave read-read-patch-patch-write-write
    /// and leave <c>LastApplied</c> holding the LOSER's map. That is not a stale activation stamp that
    /// self-heals — the next diff is computed against a map the gateway never has, so it under- or
    /// over-patches, and the drift persists until the session dies.
    /// </para>
    /// <para>
    /// Entries are removed (never disposed) in <see cref="ForgetSessionEnvState"/>: that runs while the
    /// caller still HOLDS the semaphore in the <c>session_not_found</c> branch, and disposing it under the
    /// holder would turn the release into an <see cref="ObjectDisposedException"/>.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionEnvLocks = new(StringComparer.Ordinal);

    /// <summary>
    /// Set once the gateway is observed to have no session-env route at all (a bare 404 with no
    /// <c>error_code</c> body — see <see cref="EnsureSessionEnvAsync"/>). Sticky for the process's
    /// lifetime: once tripped, every later call short-circuits to <see cref="SandboxEnvApplyResult.Unsupported"/>
    /// without another round-trip, which is also what keeps the "gateway too old" warning to exactly one
    /// log line.
    /// </summary>
    private volatile bool _sessionEnvUnsupported;

    /// <summary>Whether this process has observed the gateway to support the per-session env routes.</summary>
    public bool SessionEnvSupported => !_sessionEnvUnsupported;

    /// <summary>
    /// Whether <paramref name="ex"/> says the session-env ROUTE does not exist (gateway older than
    /// v0.1.11), as opposed to the session being gone.
    /// <para>
    /// A genuinely missing session always carries <c>error_code=session_not_found</c> (the same
    /// distinction <see cref="SandboxException.IsDefiniteMissingPath"/> draws elsewhere in the SDK), so a
    /// 404 WITHOUT that code is the route itself missing. A 405 means the path exists but not the verb,
    /// which is the same "this gateway cannot do env" conclusion and, before this predicate existed,
    /// escaped as an unhandled failure because the catch matched only <see cref="SandboxErrorKind.NotFound"/>.
    /// </para>
    /// </summary>
    private static bool IsEnvRouteAbsent(SandboxException ex) =>
        (
            ex.Kind == SandboxErrorKind.NotFound
            && !string.Equals(ex.ErrorCode, "session_not_found", StringComparison.Ordinal)
        )
        || ex.StatusCode == 405;

    /// <summary>
    /// Trips the sticky "this gateway has no env routes" flag and logs the single warning the feature is
    /// allowed (spec §6). Idempotent, so it is safe on every path that can observe the condition.
    /// </summary>
    private SandboxEnvApplyResult MarkEnvUnsupported()
    {
        if (!_sessionEnvUnsupported)
        {
            _sessionEnvUnsupported = true;
            _logger.LogWarning(
                "Sandbox gateway has no session env route; per-sandbox env is disabled for this process"
            );
        }

        return SandboxEnvApplyResult.Unsupported;
    }

    /// <summary>
    /// Seeds the env cache for a just-created session with exactly what was sent on create — called from
    /// <c>CreateSessionAsync</c> right after the session's maps are published. <paramref name="applied"/>
    /// is defensively copied so a later mutation of the caller's dictionary can never corrupt the cache.
    /// <para>
    /// The seed is deliberately UNCONFIRMED (<see cref="SessionEnvState.Confirmed"/>): it records what
    /// this process asked for, and an older gateway accepts the create while ignoring the field entirely.
    /// The first <see cref="EnsureSessionEnvAsync"/> therefore still reads the session's real env once,
    /// which is both the support probe and the correction. What the seed still buys is a
    /// <see cref="TryGetLastActivatedThread"/> entry from the moment the session exists.
    /// </para>
    /// </summary>
    private void SeedSessionEnvState(string sessionId, IReadOnlyDictionary<string, string> applied) =>
        _sessionEnv[sessionId] = new SessionEnvState(
            new Dictionary<string, string>(applied, StringComparer.Ordinal),
            null,
            Confirmed: false
        );

    /// <summary>
    /// Drops a session's env cache entry and its reconcile mutex. Called from every session-teardown path
    /// (create rollback, <c>EvictSessionStateAsync</c>, disposal) so a dead session never leaves a stale
    /// entry behind, and from <see cref="EnsureSessionEnvAsync"/> itself when the gateway reports the
    /// session gone. The mutex is removed but NOT disposed — see <see cref="_sessionEnvLocks"/>.
    /// </summary>
    private void ForgetSessionEnvState(string sessionId)
    {
        _ = _sessionEnv.TryRemove(sessionId, out _);
        _ = _sessionEnvLocks.TryRemove(sessionId, out _);
    }

    /// <summary>TEST-ONLY: forces the next <see cref="EnsureSessionEnvAsync"/> call for a session to fall
    /// back to a GET, simulating a process restart (the in-memory cache is gone but the gateway session
    /// still exists).</summary>
    internal void ForgetSessionEnvStateForTests(string sessionId) => ForgetSessionEnvState(sessionId);

    /// <summary>
    /// Reports the thread id that most recently caused this session's env to be confirmed unchanged or
    /// patched via <see cref="EnsureSessionEnvAsync"/>. Returns <see langword="false"/> when the session
    /// has no cache entry at all (never created here, already evicted, or the process restarted and
    /// <see cref="EnsureSessionEnvAsync"/> has not been called for it yet) — distinct from a
    /// <see langword="true"/> result with a <see langword="null"/> <paramref name="threadId"/>, which means
    /// the session's env was seeded (at create) but no thread has activated it since.
    /// </summary>
    public bool TryGetLastActivatedThread(string sessionId, out string? threadId)
    {
        if (_sessionEnv.TryGetValue(sessionId, out var state))
        {
            threadId = state.LastActivatedThreadId;
            return true;
        }

        threadId = null;
        return false;
    }

    /// <summary>
    /// Brings a live session's environment to exactly <paramref name="desired"/>, PATCHing only the
    /// difference from what the registry believes is currently applied. Safe (and cheap) to call on every
    /// turn: an unchanged map costs nothing beyond the local diff.
    /// </summary>
    /// <param name="sessionId">The live session to reconcile.</param>
    /// <param name="desired">The full desired env map (already merged across layers by the caller).</param>
    /// <param name="threadId">
    /// The thread driving this call — recorded via <see cref="TryGetLastActivatedThread"/> so a later
    /// caller can tell who last touched this session's env, regardless of whether this call actually
    /// PATCHed anything.
    /// </param>
    /// <param name="ct">Cancellation token observed by any gateway call this makes.</param>
    public async Task<SandboxEnvApplyResult> EnsureSessionEnvAsync(
        string sessionId,
        IReadOnlyDictionary<string, string> desired,
        string threadId,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        if (_sessionEnvUnsupported)
        {
            return SandboxEnvApplyResult.Unsupported;
        }

        // Same idiom every other per-session gateway call in this class uses — resolves the credential
        // the session was actually created under, not the process default, and reuses the cached
        // per-credential client. Deliberately NOT AcquireClientForSession, which is the create-path
        // refcount-reservation helper.
        var client = ClientFor(CredentialFor(sessionId));

        // Serialise the whole read-diff-PATCH-write for THIS session; see _sessionEnvLocks for why the
        // interleaving is corrupting rather than merely stale. Different sessions never contend.
        var gate = _sessionEnvLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // An UNCONFIRMED entry is treated exactly like a missing one: it holds what create asked for,
            // which an older gateway never applied. See SessionEnvState.Confirmed.
            if (!_sessionEnv.TryGetValue(sessionId, out var state) || !state.Confirmed)
            {
                // No usable cache entry — the process restarted and this session predates it, or the
                // session was just created and its env has not been confirmed against the gateway yet.
                // Read the session's actual current env to seed the cache before diffing.
                IReadOnlyDictionary<string, string>? current = null;
                try
                {
                    current = await client.GetEnvAsync(sessionId, ct).ConfigureAwait(false);
                }
                catch (SandboxException ex) when (IsEnvRouteAbsent(ex))
                {
                    return MarkEnvUnsupported();
                }
                catch (SandboxException ex)
                    when (ex.Kind == SandboxErrorKind.NotFound
                        && string.Equals(ex.ErrorCode, "session_not_found", StringComparison.Ordinal)
                    )
                {
                    ForgetSessionEnvState(sessionId);
                    return SandboxEnvApplyResult.SessionGone;
                }
                catch (SandboxException ex) when (state is not null)
                {
                    // The confirming read failed for a reason that is neither "no such route" nor "no such
                    // session" — a timeout, a 5xx, a malformed body. This read is a CORRECTION, not the
                    // operation the caller asked for, and this session already has a seed from its own
                    // create, so carry on against that rather than failing the caller: this runs inside
                    // agent construction, and throwing here would stop a conversation starting over a
                    // reconciliation detail. The entry stays UNCONFIRMED, so the next activation retries.
                    _logger.LogDebug(
                        ex,
                        "Could not confirm sandbox session {SessionId} env against the gateway; using the create-time map for this reconcile",
                        sessionId
                    );
                }

                state = new SessionEnvState(
                    new Dictionary<string, string>(current ?? state!.LastApplied, StringComparer.Ordinal),
                    // Keep whatever activation stamp the seed/previous entry carried; this read corrects
                    // the MAP, and says nothing about who last activated the session.
                    state?.LastActivatedThreadId,
                    Confirmed: current is not null
                );
                _sessionEnv[sessionId] = state;
            }

            var diff = SandboxEnvRules.Diff(state.LastApplied, desired);
            if (diff.Count == 0)
            {
                // Under the gate, `state` is still the current entry, so this cannot write back a
                // superseded LastApplied. Only the activation stamp changes here.
                _sessionEnv[sessionId] = state with
                {
                    LastActivatedThreadId = threadId,
                };
                return SandboxEnvApplyResult.Unchanged;
            }

            IReadOnlyDictionary<string, string> patched;
            try
            {
                patched = await client.PatchEnvAsync(sessionId, diff, ct).ConfigureAwait(false);
            }
            catch (SandboxException ex) when (IsEnvRouteAbsent(ex))
            {
                // The seeding GET above is not the only way to meet an old gateway: a gateway that
                // serves GET /env but not PATCH answers 405 here. Without this the failure escaped to
                // the caller — a 500 on workspace edit, and a throw out of the agent build, which calls
                // this synchronously.
                return MarkEnvUnsupported();
            }
            catch (SandboxException ex)
                when (ex.Kind == SandboxErrorKind.NotFound
                    && string.Equals(ex.ErrorCode, "session_not_found", StringComparison.Ordinal)
                )
            {
                ForgetSessionEnvState(sessionId);
                return SandboxEnvApplyResult.SessionGone;
            }

            // Every other SandboxException (InvalidEnv, transport, ...)
            // deliberately propagates uncaught — the caller is expected to catch and decide (e.g. InvalidEnv
            // means ITS desired map is malformed, which retrying here could never fix).
            _sessionEnv[sessionId] = new SessionEnvState(
                new Dictionary<string, string>(patched, StringComparer.Ordinal),
                threadId,
                Confirmed: true
            );

            _logger.LogInformation(
                "Sandbox session {SessionId} env patched by thread {ThreadId}: set {SetKeys}; unset {UnsetKeys}",
                sessionId,
                threadId,
                string.Join(", ", diff.Where(kv => kv.Value is not null).Select(kv => kv.Key)),
                string.Join(", ", diff.Where(kv => kv.Value is null).Select(kv => kv.Key))
            );

            return SandboxEnvApplyResult.Patched;
        }
        finally
        {
            gate.Release();
        }
    }
}
