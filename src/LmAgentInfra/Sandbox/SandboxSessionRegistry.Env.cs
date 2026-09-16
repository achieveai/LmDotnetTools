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
    private sealed record SessionEnvState(
        IReadOnlyDictionary<string, string> LastApplied,
        string? LastActivatedThreadId
    );

    /// <summary>
    /// Per-session env cache. Reads/writes of a single entry are LAST-WRITE-WINS — there is no per-session
    /// lock — which is acceptable: <see cref="EnsureSessionEnvAsync"/> is a best-effort reconciler a caller
    /// re-invokes on its own cadence (e.g. once per turn), so a lost update under concurrent callers for the
    /// SAME session id self-heals on the next call rather than needing to be prevented here.
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionEnvState> _sessionEnv = new(StringComparer.Ordinal);

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
    /// </summary>
    private void SeedSessionEnvState(string sessionId, IReadOnlyDictionary<string, string> applied) =>
        _sessionEnv[sessionId] = new SessionEnvState(
            new Dictionary<string, string>(applied, StringComparer.Ordinal),
            null
        );

    /// <summary>
    /// Drops a session's env cache entry. Called from every session-teardown path (create rollback,
    /// <c>EvictSessionStateAsync</c>, disposal) so a dead session never leaves a stale entry behind, and
    /// from <see cref="EnsureSessionEnvAsync"/> itself when the gateway reports the session gone.
    /// </summary>
    private void ForgetSessionEnvState(string sessionId) => _ = _sessionEnv.TryRemove(sessionId, out _);

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

        if (!_sessionEnv.TryGetValue(sessionId, out var state))
        {
            // No cache entry — e.g. the process restarted and this session predates it. Read the
            // session's actual current env from the gateway to seed the cache before diffing.
            IReadOnlyDictionary<string, string> current;
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

            state = new SessionEnvState(new Dictionary<string, string>(current, StringComparer.Ordinal), null);
            _sessionEnv[sessionId] = state;
        }

        var diff = SandboxEnvRules.Diff(state.LastApplied, desired);
        if (diff.Count == 0)
        {
            // Last-write-wins (see _sessionEnv doc comment): only the activation stamp changes here.
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
            // The seeding GET above is not the only way to meet an old gateway: a session created
            // before this process decided env was supported reaches PATCH with a cache entry and no
            // probe, and a gateway that serves GET /env but not PATCH answers 405 here. Without this
            // the failure escaped to the caller — a 500 on workspace edit, and a throw out of the
            // agent build, which calls this synchronously.
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
            threadId
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
}
