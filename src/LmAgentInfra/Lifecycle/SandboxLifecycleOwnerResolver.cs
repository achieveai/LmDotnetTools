using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The default <see cref="ILifecycleOwnerResolver"/>: it answers "whose conversation is this?" from
/// the sandbox binding a thread was established under, which is the host's own record of who asked
/// for the conversation in the first place.
/// <para>
/// <b>Only <see cref="SandboxEstablishedBinding.CallerCredential"/> confers ownership</b>, never
/// <see cref="SandboxEstablishedBinding.Credential"/>. The distinction is the whole point of this
/// class and is easy to get backwards, because the effective credential is always populated and the
/// caller credential often is not. An interactive UI conversation has no caller credential and runs
/// under the process default app id; a service-to-service conversation carries the calling app's own
/// credential. If ownership were read from the effective credential, every interactive conversation
/// in the process would resolve to the default app id — and any S2S subscriber that happened to
/// authenticate as that same app would then receive lifecycle events for the interactive user's
/// conversations. The binding's own documentation warns against exactly this conflation; reading the
/// caller credential and nothing else is how that warning is enforced rather than merely noted.
/// </para>
/// <para>
/// The consequence is deliberate: interactive conversations have <em>no</em> remote owner and are
/// never delivered off-box. That is the fail-closed direction. A host that wants interactive traffic
/// observable must make that an explicit decision with its own resolver, not inherit it from a
/// fallback.
/// </para>
/// <para>
/// <b>The registry is reached through a factory, and that is load-bearing.</b> A host registers the
/// registry so it can publish its own lifecycle events, which means the registry depends on the
/// publisher, which is the delivery pipeline, which depends on this resolver — so an eager
/// constructor dependency closes a container-level cycle and the host hangs on startup with the
/// delivery flag on. Deferring the lookup to the first event breaks it, and costs nothing: ownership
/// is a question about a binding that exists only once a conversation is running.
/// </para>
/// </summary>
public sealed class SandboxLifecycleOwnerResolver : ILifecycleOwnerResolver
{
    private readonly Func<SandboxSessionRegistry> _sessions;

    /// <summary>Creates a resolver over the registry that holds the host's thread bindings.</summary>
    /// <param name="sessions">The registry conversations publish their established binding to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public SandboxLifecycleOwnerResolver(SandboxSessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = () => sessions;
    }

    /// <summary>
    /// Creates a resolver that looks the registry up on first use rather than holding one.
    /// </summary>
    /// <param name="sessions">Returns the registry conversations publish their established binding
    /// to. Called on the first ownership question and on every one after it, so a host that resolves
    /// from a container gets the container's own caching rather than a second registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    /// <remarks>
    /// This is the overload the container registration uses; see the cycle note on the class.
    /// </remarks>
    public SandboxLifecycleOwnerResolver(Func<SandboxSessionRegistry> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        _sessions = sessions;
    }

    /// <inheritdoc />
    public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
        LifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        cancellationToken.ThrowIfCancellationRequested();

        var correlation = lifecycleEvent.Correlation;
        if (correlation is null)
        {
            // An event with no correlation cannot be attributed to a conversation, and an
            // unattributable event has no owner entitled to see it.
            return ValueTask.FromResult<LifecycleOwnerKey?>(null);
        }

        // A sub-agent runs on its own thread, and depending on its mode that thread may never
        // establish a binding of its own. Falling back to the spawning thread keeps a sub-agent's
        // events attributed to the caller who is ultimately responsible for them; without it every
        // sub-agent event would be silently dropped, which reads as a delivery bug rather than as
        // the policy decision it would actually be.
        //
        // This is not inference from the envelope: both ids are used only as keys into the host's
        // own binding map, and a thread that is not in that map yields nothing regardless of what
        // the envelope claims.
        return ValueTask.FromResult(OwnerOf(correlation.ThreadId) ?? OwnerOf(correlation.ParentThreadId));
    }

    /// <inheritdoc />
    public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
        string? threadId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No parent fallback here, deliberately. This method scopes an approval, and an approval must
        // be answerable only by the owner of the thread whose tool call is actually being gated. The
        // event path can afford to widen to the spawning thread because it only decides who may
        // observe; this path decides who may authorize, and inheriting that upward would let a parent
        // conversation's subscriber approve a call it was never shown.
        return ValueTask.FromResult(OwnerOf(threadId));
    }

    /// <inheritdoc />
    public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(string appId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No allow-list of known app ids is consulted, and that is safe rather than lax: the caller
        // has already been authenticated as this app, and mapping it to its own owner key grants it
        // nothing by itself. What an owner key can actually reach is decided on the other side — a
        // conversation resolves to an owner only if that app created it. An unknown app therefore
        // gets a well-formed key that matches no conversation, which is the same as getting nothing.
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(appId) ? null : LifecycleOwnerKey.ForAppId(appId));
    }

    private LifecycleOwnerKey? OwnerOf(string? threadId)
    {
        if (!_sessions().TryGetEstablishedBinding(threadId, out var binding))
        {
            return null;
        }

        var caller = binding?.CallerCredential;
        return caller is { } credential && !string.IsNullOrWhiteSpace(credential.AppId)
            ? LifecycleOwnerKey.ForCredential(credential)
            : null;
    }
}
