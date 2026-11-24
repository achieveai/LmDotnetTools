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
/// meanwhile cannot lose it: the next release of the owner and a workspace release or destroy delete it. Disposal
/// deletes nothing.
/// </para>
/// <para>
/// Releases of one owner run one at a time, in call order, and each runs to the end under
/// <see cref="CancellationToken.None"/>; a caller's token only stops the caller waiting. So a release joins the one
/// already running (a conversation delete's, say) instead of racing it with a second DELETE, and its
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
    /// <see cref="ReleaseOwnedSessionsAsync(string, string, Func{Task}, CancellationToken)"/>). The owner's creates wait for it.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> _ownerFences = new(StringComparer.Ordinal);

    /// <summary>
    /// Per owner, the working directory its last committed move left. Set before that release ends, so every create
    /// that waited for it, and every later one, reads it (see <see cref="ResolveOwnerMove"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _ownerMovedTo = new(StringComparer.Ordinal);

    /// <summary>
    /// Stops the dedicated sandboxes <paramref name="threadId"/> owns and confirms each is gone. Only the container
    /// goes: the workspace files are the gateway's mount and stay, and the conversation keeps its history; its next
    /// turn creates a fresh sandbox. A no-op when the thread owns none. See the type remarks for ordering and
    /// cancellation.
    /// </summary>
    /// <exception cref="SandboxException">A sandbox could not be stopped; it is still tracked, so a retry deletes it.</exception>
    public Task ReleaseOwnedSessionsAsync(string threadId, CancellationToken ct = default) =>
        ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir: null, commit: null, ct);

    /// <summary>
    /// As <see cref="ReleaseOwnedSessionsAsync(string, CancellationToken)"/>, and then, only when every sandbox is
    /// confirmed gone, runs <paramref name="commit"/> (the caller storing <paramref name="movedToWorkingDir"/> as the
    /// owner's working directory). Until that release ends the owner's next sandbox is held back, so it is created from
    /// what was committed, or, when the release fails, from what was there before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The take-out happens before this returns. Call it where no new agent for the owner can be admitted (the agent
    /// pool's per-thread lock), so the release covers exactly the sandboxes of the agents already gone.
    /// </para>
    /// <para>
    /// Once committed, an owner create that still asks for another directory is re-resolved to
    /// <paramref name="movedToWorkingDir"/>. Such a ref was captured before the move (by a refresh, a liveness
    /// recreate or a tool call of an agent built earlier), and must not put the old directory back in the slot.
    /// </para>
    /// </remarks>
    /// <param name="threadId">The owner.</param>
    /// <param name="movedToWorkingDir">The working directory <paramref name="commit"/> stores, in the form the
    /// owner's <see cref="WorkspaceRef.WorkingDir"/> carries.</param>
    /// <param name="commit">Stores the move. Runs only after every sandbox is confirmed gone.</param>
    /// <param name="ct">Stops the caller waiting; the release itself runs to the end.</param>
    /// <exception cref="SandboxException">A sandbox could not be stopped; nothing was committed.</exception>
    public Task ReleaseOwnedSessionsAsync(
        string threadId,
        string movedToWorkingDir,
        Func<Task> commit,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(movedToWorkingDir);
        ArgumentNullException.ThrowIfNull(commit);
        return ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir, commit, ct);
    }

    private Task ReleaseOwnedSessionsCoreAsync(
        string threadId,
        string? movedToWorkingDir,
        Func<Task>? commit,
        CancellationToken ct
    )
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
            release = Task.Run(() => RunOwnerReleaseAsync(threadId, previous, movedToWorkingDir, commit));
            _ownerReleaseTails[threadId] = release;
            if (commit is not null)
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

    private async Task RunOwnerReleaseAsync(
        string threadId,
        Task previous,
        string? movedToWorkingDir,
        Func<Task>? commit
    )
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

        if (commit is not null)
        {
            await commit().ConfigureAwait(false);
            // Before this release ends: the creates waiting on its fence re-resolve to it as soon as they wake.
            _ownerMovedTo[threadId] = movedToWorkingDir!;
        }
    }

    /// <summary>
    /// The owner create <paramref name="workspaceRef"/> should run: re-resolved to the directory the owner's last
    /// committed move left when it asks for another. A ref with no owner, or an owner that never moved, is unchanged.
    /// </summary>
    /// <remarks>Call under <see cref="_ownerReleaseLock"/> with the slot lookup, so no take-out falls in between.</remarks>
    private WorkspaceRef ResolveOwnerMove(WorkspaceRef workspaceRef)
    {
        if (
            workspaceRef.OwnerThreadId is not { } owner
            || !_ownerMovedTo.TryGetValue(owner, out var movedTo)
            || string.Equals(workspaceRef.WorkingDir, movedTo, StringComparison.Ordinal)
        )
        {
            return workspaceRef;
        }

        _logger.LogInformation(
            "Sandbox create for owner {OwnerThreadId} asked for working directory {StaleWorkingDir}, captured before "
                + "the owner moved; creating in {WorkingDir}",
            owner,
            workspaceRef.WorkingDir,
            movedTo
        );
        return workspaceRef with { WorkingDir = movedTo };
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

        // Pinned for the DELETE: a liveness eviction of this session meanwhile (a refresh that read its 404) drops
        // the session's own refcount, and the last one disposes the client, cancelling the DELETE in flight.
        var credential = CredentialFor(session.SessionId);
        var client = AcquireClientForSession(credential);
        try
        {
            await client.DeleteAsync(session.SessionId, CancellationToken.None).ConfigureAwait(false);
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
        finally
        {
            DecrementSessionRefAndMaybeDispose(credential);
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
