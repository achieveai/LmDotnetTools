using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Release of the dedicated sandboxes a conversation owns (<see cref="WorkspaceRef.OwnerThreadId"/>).
/// </summary>
/// <remarks>
/// <para>
/// A release takes the owner's sessions out of their reusable slots at once, synchronously, and only then deletes
/// them. From that moment the owner's next create gets a fresh sandbox, never one being deleted, and a release never
/// deletes a sandbox created after it was called.
/// </para>
/// <para>
/// A taken-out session is "retiring": it is tracked apart from the slots until the gateway confirms its DELETE (a
/// <c>404</c> counts as gone). A refused DELETE, a caller that stops waiting, or a fresh sandbox taking the slot
/// meanwhile cannot lose it: the next release of the owner, a workspace release or destroy, and disposal all delete
/// it.
/// </para>
/// <para>
/// Releases of one owner run one at a time, in call order, and each runs to the end under
/// <see cref="CancellationToken.None"/>; a caller's token only stops the caller waiting. So a release joins the one
/// already running (the one an agent removal started, say) instead of racing it with a second DELETE, and its
/// outcome is the gateway's real answer.
/// </para>
/// </remarks>
public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// Sessions an owner release took out of <see cref="_sessions"/> whose DELETE is not confirmed yet. Keyed by the
    /// creation slot itself (reference identity), so a create still in flight is tracked too, with the key it was
    /// published under.
    /// </summary>
    private readonly ConcurrentDictionary<
        Lazy<Task<SandboxSession>>,
        (string WorkspaceId, string AppId, bool ReadOnlyEgress, string? OwnerThreadId)
    > _retiringSessions = new();

    /// <summary>Guards <see cref="_ownerReleaseTails"/> and makes take-out plus enqueue one step.</summary>
    private readonly object _ownerReleaseLock = new();

    /// <summary>The last release queued per owner; the next one runs after it. Guarded by <see cref="_ownerReleaseLock"/>.</summary>
    private readonly Dictionary<string, Task> _ownerReleaseTails = new(StringComparer.Ordinal);

    /// <summary>
    /// Per owner, a running release that commits a change (see
    /// <see cref="ReleaseOwnedSessionsAsync(string, Func{Task}?, CancellationToken)"/>). The owner's creates wait for it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> _ownerFences = new(StringComparer.Ordinal);

    /// <summary>
    /// Stops the dedicated sandboxes <paramref name="threadId"/> owns and confirms each is gone. Only the container
    /// goes: the workspace files are the gateway's mount and stay, and the conversation keeps its history; its next
    /// turn creates a fresh sandbox. A no-op when the thread owns none. See the type remarks for ordering and
    /// cancellation.
    /// </summary>
    /// <exception cref="SandboxException">A sandbox could not be stopped; it is still tracked, so a retry deletes it.</exception>
    public Task ReleaseOwnedSessionsAsync(string threadId, CancellationToken ct = default) =>
        ReleaseOwnedSessionsAsync(threadId, afterRetired: null, ct);

    /// <summary>
    /// As <see cref="ReleaseOwnedSessionsAsync(string, CancellationToken)"/>, and then, only when every sandbox is
    /// confirmed gone, runs <paramref name="afterRetired"/> (a caller's commit, such as a new working directory).
    /// Until that release ends the owner's next sandbox is held back, so it is created from what was committed, or,
    /// when the release fails, from what was there before.
    /// </summary>
    /// <remarks>
    /// The take-out happens before this returns. Call it where no new agent for the owner can be admitted (the agent
    /// pool's per-thread lock), so the release covers exactly the sandboxes of the agents already gone.
    /// </remarks>
    /// <exception cref="SandboxException">A sandbox could not be stopped; nothing was committed.</exception>
    public Task ReleaseOwnedSessionsAsync(string threadId, Func<Task>? afterRetired, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        Task release;
        lock (_ownerReleaseLock)
        {
            foreach (var entry in _sessions)
            {
                if (
                    string.Equals(entry.Key.OwnerThreadId, threadId, StringComparison.Ordinal)
                    && _sessions.TryRemove(entry)
                )
                {
                    _retiringSessions[entry.Value] = entry.Key;
                }
            }

            var previous = _ownerReleaseTails.GetValueOrDefault(threadId) ?? Task.CompletedTask;
            // On the thread pool: the caller may hold a lock, and the deletes are I/O.
            release = Task.Run(() => RunOwnerReleaseAsync(threadId, previous, afterRetired));
            _ownerReleaseTails[threadId] = release;
            if (afterRetired is not null)
            {
                _ownerFences[threadId] = release;
            }
        }

        _ = release.ContinueWith(
            finished => ForgetOwnerRelease(threadId, finished),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        return ct.CanBeCanceled ? release.WaitAsync(ct) : release;
    }

    /// <summary>
    /// Waits until no release that commits a change for <paramref name="threadId"/> is running, so a caller that reads
    /// the owner's committed state next (its working directory, say) reads the outcome, not the state the release is
    /// replacing. Completes at once when none is running. A failed release committed nothing, so its failure is not
    /// rethrown here.
    /// </summary>
    /// <remarks>
    /// Covers a thread that owns no sandbox yet too (a shared-sandbox thread moving to a dedicated one): the fence is
    /// keyed by the thread, not by its sessions.
    /// </remarks>
    public async Task WaitForOwnerMoveAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (!_ownerFences.TryGetValue(threadId, out var fence))
        {
            return;
        }

        try
        {
            await fence.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failed release committed nothing: what was there before still holds.
        }
    }

    private async Task RunOwnerReleaseAsync(string threadId, Task previous, Func<Task>? afterRetired)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // That release reported its own outcome to its callers; whatever it left is retried below.
        }

        var failures = 0;
        SandboxException? lastFailure = null;
        foreach (var (retiring, key) in _retiringSessions.ToList())
        {
            if (!string.Equals(key.OwnerThreadId, threadId, StringComparison.Ordinal))
            {
                continue;
            }

            if (await TryDeleteRetiringAsync(retiring, key).ConfigureAwait(false) is { } refused)
            {
                failures++;
                lastFailure = refused;
            }
        }

        if (lastFailure is not null)
        {
            throw ReleaseFailure(failures, $"of thread '{threadId}'", lastFailure);
        }

        if (afterRetired is not null)
        {
            await afterRetired().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes one retiring session and forgets it once the gateway confirms. Returns the refusal, leaving it tracked,
    /// when the gateway refuses.
    /// </summary>
    private async Task<SandboxException?> TryDeleteRetiringAsync(
        Lazy<Task<SandboxSession>> retiring,
        (string WorkspaceId, string AppId, bool ReadOnlyEgress, string? OwnerThreadId) key
    )
    {
        SandboxSession session;
        try
        {
            // Usually complete. A create still in flight is awaited, then deleted: skipped, it would live on untracked.
            session = await retiring.Value.ConfigureAwait(false);
        }
        catch
        {
            // A create that failed left no sandbox.
            _ = _retiringSessions.TryRemove(retiring, out _);
            return null;
        }

        try
        {
            await ClientFor(CredentialFor(session.SessionId))
                .DeleteAsync(session.SessionId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (SandboxException ex) when (ex.StatusCode == 404)
        {
            // Already gone (deleted elsewhere, expired, or purged).
        }
        catch (SandboxException ex)
        {
            _logger.LogWarning(
                "Sandbox release returned {StatusCode} for session {SessionId} of owner {OwnerThreadId}; it stays "
                    + "tracked for a retry",
                ex.StatusCode,
                session.SessionId,
                key.OwnerThreadId
            );
            return ex;
        }

        // Only the caller that forgets it also clears its state: a workspace release may have deleted it meanwhile.
        if (_retiringSessions.TryRemove(retiring, out _))
        {
            _logger.LogInformation(
                "Released sandbox session {SessionId} of owner {OwnerThreadId}",
                session.SessionId,
                key.OwnerThreadId
            );
            _ = _replacedSessions.TryRemove(key, out _);
            await EvictSessionStateAsync(session).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<SandboxSession> GetOrCreateAfterOwnerFenceAsync(
        Task fence,
        WorkspaceRef workspaceRef,
        CancellationToken ct,
        SandboxCredential? credential
    )
    {
        try
        {
            await fence.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A failed release committed nothing: the owner's sandbox is created from what was there before.
        }

        return await GetOrCreateSessionAsync(workspaceRef, ct, credential).ConfigureAwait(false);
    }

    private void ForgetOwnerRelease(string threadId, Task finished)
    {
        // Observed here, so a release nobody waits for any more (a cancelled caller) is not an unobserved fault.
        _ = finished.Exception;
        lock (_ownerReleaseLock)
        {
            if (_ownerReleaseTails.TryGetValue(threadId, out var tail) && ReferenceEquals(tail, finished))
            {
                _ = _ownerReleaseTails.Remove(threadId);
            }
        }

        _ = _ownerFences.TryRemove(new KeyValuePair<string, Task>(threadId, finished));
    }
}
