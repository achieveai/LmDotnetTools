using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Release of the dedicated sandboxes a conversation owns (<see cref="WorkspaceRef.OwnerThreadId"/>).
/// </summary>
/// <remarks>
/// <para>
/// A release is a delete or a retire. A delete (the conversation is going) deletes the owner's sandboxes on the
/// gateway, including the retired ones and a current one a restart forgot, which the owner's records name. A retire
/// (the conversation moves) deletes nothing: it marks the owner's records retired and drops the sessions from this
/// host, so they wait on the gateway until the conversation's own delete.
/// </para>
/// <para>
/// A release takes the owner's sessions out of their reusable slots at once, synchronously, and only then deletes
/// or retires them. From that moment the owner's next create gets a fresh sandbox, never one being released, and a
/// release never touches a sandbox created after it was called.
/// </para>
/// <para>
/// A session a delete took out is "retiring": it is tracked apart from the slots until the gateway confirms its
/// DELETE (a <c>404</c> counts as gone). A refused DELETE, a caller that stops waiting, or a fresh sandbox taking the
/// slot meanwhile cannot lose it: the next release of the owner and a workspace release or destroy delete it.
/// Disposal deletes nothing.
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
    /// Per owner, the running release of a conversation being deleted. Its confirmed deletes leave the owner's records
    /// alone: the conversation's own delete races them, and a record write that loaded the metadata just before that
    /// delete would write the deleted conversation back.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> _ownersBeingDeleted = new(StringComparer.Ordinal);

    /// <summary>
    /// Stops the dedicated sandboxes <paramref name="threadId"/> owns and confirms each is gone. Only the container
    /// goes: the workspace files are the gateway's mount and stay, and the conversation keeps its history; its next
    /// turn creates a fresh sandbox. A no-op when the thread owns none. See the type remarks for ordering and
    /// cancellation.
    /// </summary>
    /// <exception cref="SandboxException">A sandbox could not be stopped; it is still tracked, so a retry deletes it.</exception>
    public Task ReleaseOwnedSessionsAsync(string threadId, CancellationToken ct = default) =>
        ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir: null, commit: null, OwnerReleaseKind.Delete, ct);

    /// <summary>
    /// As <see cref="ReleaseOwnedSessionsAsync(string, CancellationToken)"/>, and also deletes the sandboxes
    /// <paramref name="persisted"/> records that this host no longer tracks: the current one a restart forgot and the
    /// retired ones a move left. The conversation is being deleted, so these go with it. Each is deleted under the
    /// credential of the app it was created under: <paramref name="callerCredential"/> or this host's own, whichever
    /// has that app id. One neither has is logged and left on the gateway.
    /// </summary>
    /// <param name="threadId">The owner.</param>
    /// <param name="persisted">The owner's records, read before its conversation is deleted.</param>
    /// <param name="callerCredential">The credential of the caller deleting the conversation, if it sent one.</param>
    /// <param name="ct">Stops the caller waiting; the release itself runs to the end.</param>
    /// <exception cref="SandboxException">A sandbox could not be stopped; it is still tracked, so a retry deletes it.</exception>
    public Task ReleaseOwnedSessionsAsync(
        string threadId,
        IReadOnlyList<OwnedSandboxRecord> persisted,
        SandboxCredential? callerCredential,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return ReleaseOwnedSessionsCoreAsync(
            threadId,
            movedToWorkingDir: null,
            commit: null,
            OwnerReleaseKind.Delete,
            ct,
            persisted,
            callerCredential
        );
    }

    /// <summary>
    /// Retires the dedicated sandboxes <paramref name="threadId"/> owns: this host stops using them and sends the
    /// gateway nothing, so no DELETE wipes the workspace's command output for the other sandboxes on it (SBX#372).
    /// Each stays paired with the conversation, recorded as retired, and sleeps on the gateway until the conversation
    /// is deleted. Its webhook secret stays too, for a command it still runs. The owner's next turn creates a fresh
    /// sandbox. A no-op on the gateway when the thread owns none.
    /// </summary>
    public Task RetireOwnedSessionsAsync(string threadId, CancellationToken ct = default) =>
        ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir: null, commit: null, OwnerReleaseKind.Retire, ct);

    /// <summary>
    /// As <see cref="RetireOwnedSessionsAsync(string, CancellationToken)"/>, and then runs <paramref name="commit"/>
    /// (the caller storing <paramref name="movedToWorkingDir"/>), with the same hold on the owner's next sandbox as
    /// <see cref="ReleaseOwnedSessionsAsync(string, string, Func{Task}, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="Exception">The retirement could not be recorded; nothing was committed.</exception>
    public Task RetireOwnedSessionsAsync(
        string threadId,
        string movedToWorkingDir,
        Func<Task> commit,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(movedToWorkingDir);
        ArgumentNullException.ThrowIfNull(commit);
        return ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir, commit, OwnerReleaseKind.Retire, ct);
    }

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
        return ReleaseOwnedSessionsCoreAsync(threadId, movedToWorkingDir, commit, OwnerReleaseKind.Delete, ct);
    }

    private enum OwnerReleaseKind
    {
        /// <summary>Deletes the owner's sandboxes on the gateway.</summary>
        Delete,

        /// <summary>Stops using them, records them as retired and sends the gateway nothing.</summary>
        Retire,
    }

    private Task ReleaseOwnedSessionsCoreAsync(
        string threadId,
        string? movedToWorkingDir,
        Func<Task>? commit,
        OwnerReleaseKind kind,
        CancellationToken ct,
        IReadOnlyList<OwnedSandboxRecord>? persisted = null,
        SandboxCredential? callerCredential = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        Task release;
        lock (_ownerReleaseLock)
        {
            var retired =
                new List<(
                    Lazy<Task<SandboxSession>> Slot,
                    (string WorkspaceId, string AppId, bool ReadOnlyEgress, string? OwnerThreadId) Key
                )>();
            foreach (var entry in _sessions)
            {
                if (
                    string.Equals(entry.Key.OwnerThreadId, threadId, StringComparison.Ordinal)
                    && _sessions.TryRemove(entry)
                )
                {
                    if (kind == OwnerReleaseKind.Retire)
                    {
                        retired.Add((entry.Value, entry.Key));
                    }
                    else
                    {
                        _retiringSessions[entry.Value] = entry.Key;
                    }
                }
            }

            if (persisted is not null)
            {
                TrackPersistedForDelete(threadId, persisted, callerCredential);
                // Marked before the release can run; replaced by the release itself below.
                _ownersBeingDeleted[threadId] = Task.CompletedTask;
            }

            var previous = _ownerReleaseTails.GetValueOrDefault(threadId) ?? Task.CompletedTask;
            // On the thread pool: the caller may hold a lock, and the deletes are I/O.
            release =
                kind == OwnerReleaseKind.Retire
                    ? Task.Run(() => RunOwnerRetireAsync(threadId, previous, retired, movedToWorkingDir, commit))
                    : Task.Run(() => RunOwnerReleaseAsync(threadId, previous, movedToWorkingDir, commit));
            _ownerReleaseTails[threadId] = release;
            if (commit is not null)
            {
                _ownerFences[threadId] = release;
            }

            if (persisted is not null)
            {
                _ownersBeingDeleted[threadId] = release;
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

    private async Task RunOwnerRetireAsync(
        string threadId,
        Task previous,
        IReadOnlyList<(
            Lazy<Task<SandboxSession>> Slot,
            (string WorkspaceId, string AppId, bool ReadOnlyEgress, string? OwnerThreadId) Key
        )> retired,
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
            // That release reported its own outcome to its callers.
        }

        var sessions =
            new List<(
                SandboxSession Session,
                (string WorkspaceId, string AppId, bool ReadOnlyEgress, string? OwnerThreadId) Key
            )>();
        foreach (var (slot, key) in retired)
        {
            try
            {
                // Usually complete. A create still in flight is awaited, then retired: skipped, it would live on as the
                // owner's current sandbox in a directory the owner moved away from. Its record is written by then.
                sessions.Add((await slot.Value.ConfigureAwait(false), key));
            }
            catch
            {
                // A create that failed left no sandbox.
            }
        }

        try
        {
            // Every record, not only the sessions taken out above: a current one a restart forgot is just as old.
            // Recorded before the sessions are evicted, as a delete is confirmed before them: the eviction releases
            // their client, which ends any call still in flight on them, such as a liveness probe.
            if (_ownedSandboxes is not null)
            {
                await _ownedSandboxes
                    .UpdateAsync(
                        threadId,
                        records => [.. records.Select(record => record with { Retired = true })],
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            // Out of their slots either way, so out of every other map too. Unrecorded, a session stays its owner's
            // current one, which the owner's next turn reuses.
            foreach (var (session, key) in sessions)
            {
                _ = _replacedSessions.TryRemove(key, out _);
                await EvictSessionStateAsync(session, retire: true).ConfigureAwait(false);
            }
        }

        foreach (var (session, _) in sessions)
        {
            _logger.LogInformation(
                "Retired sandbox session {SessionId} of owner {OwnerThreadId}: no DELETE, it stays on the gateway "
                    + "until the conversation is deleted",
                session.SessionId,
                threadId
            );
        }

        if (commit is not null)
        {
            await commit().ConfigureAwait(false);
            _ownerMovedTo[threadId] = movedToWorkingDir!;
        }
    }

    /// <summary>
    /// Tracks, as retiring under <paramref name="owner"/>, every sandbox <paramref name="persisted"/> records that this
    /// host does not track already, so the owner's delete deletes it with the rest. Call under
    /// <see cref="_ownerReleaseLock"/>, with the owner's take-out.
    /// </summary>
    private void TrackPersistedForDelete(
        string owner,
        IReadOnlyList<OwnedSandboxRecord> persisted,
        SandboxCredential? callerCredential
    )
    {
        var known = _retiringSessions
            .Keys.Where(slot => slot.IsValueCreated && slot.Value.IsCompletedSuccessfully)
            .Select(slot => slot.Value.Result.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var record in persisted)
        {
            if (_sessionsById.ContainsKey(record.SessionId) || known.Contains(record.SessionId))
            {
                continue;
            }

            SandboxCredential? found =
                callerCredential is { } caller && string.Equals(caller.AppId, record.AppId, StringComparison.Ordinal)
                    ? caller
                : string.Equals(_defaultCredential.AppId, record.AppId, StringComparison.Ordinal) ? _defaultCredential
                : null;
            if (found is not { } credential)
            {
                _logger.LogWarning(
                    "Cannot delete sandbox session {SessionId} of owner {OwnerThreadId}: it was created under app "
                        + "{AppId}, and neither the caller nor this host has that app's credential. It stays on the gateway",
                    record.SessionId,
                    owner,
                    record.AppId
                );
                continue;
            }

            // Registered as a session of that credential, so the delete below resolves it and the eviction after the
            // DELETE releases the client reference taken here.
            _ = AcquireClientForSession(credential);
            _sessionCredentials[record.SessionId] = credential;
            var session = new SandboxSession(
                record.WorkspaceId,
                record.SessionId,
                record.GatewayWorkspace,
                HostPath: string.Empty,
                WorkspaceWorkingDir: record.WorkingDir,
                ReadOnlyEgress: record.ReadOnlyEgress,
                OwnerThreadId: owner
            );
            _retiringSessions[new Lazy<Task<SandboxSession>>(Task.FromResult(session))] = (
                record.WorkspaceId,
                record.AppId,
                record.ReadOnlyEgress,
                owner
            );
            _logger.LogInformation(
                "Deleting sandbox session {SessionId} of owner {OwnerThreadId} that this host no longer tracked "
                    + "(retired: {Retired})",
                record.SessionId,
                owner,
                record.Retired
            );
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
        _ = _ownersBeingDeleted.TryRemove(new KeyValuePair<string, Task>(threadId, finished));
    }
}
