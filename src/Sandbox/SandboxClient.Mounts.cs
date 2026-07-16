using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Single-flight cache of each session's writable workspace mount id (<c>session_mounts.id</c>) — the
    /// integer every direct file/command API is keyed by. The value is a <see cref="Lazy{T}"/> over the
    /// in-flight resolution <see cref="Task{T}"/>, so concurrent COLD callers for one session share ONE
    /// resolution <c>GET</c> instead of each issuing their own. Populated lazily because the public
    /// command/file methods receive only a session id (a session may have been created by a different
    /// client or registry), so the mount id must be discoverable on demand; cached because it is stable
    /// for a session's lifetime (the gateway assigns it at create and never renumbers it).
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<long>>> _workspaceMountIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Seeds <see cref="_workspaceMountIds"/> from a create/get result that already carries the
    /// workspace mount id (installing an already-resolved entry), so the first
    /// <see cref="ExecuteAsync"/>/file call after a create does not pay a redundant <c>GET</c> to
    /// rediscover it. A no-op when the info has no mount id (e.g. a list result, which carries no volumes).
    /// </summary>
    private void SeedWorkspaceMountId(SandboxInfo info)
    {
        if (info.WorkspaceMountId is { } mountId)
        {
            _workspaceMountIds[info.SessionId] = new Lazy<Task<long>>(() => Task.FromResult(mountId));
        }
    }

    /// <summary>
    /// Resolves the writable workspace mount id for <paramref name="sessionId"/>, returning the
    /// cached value when present or fetching it once via <c>GET /api/v1/sandboxes/{session_id}</c>
    /// (<c>volumes.workspace.id</c>). Every direct file/command call routes through here so a caller
    /// never has to know the mount id. Resolution is SINGLE-FLIGHT: concurrent cold callers share one
    /// in-flight <c>GET</c>. The shared fetch runs on <see cref="CancellationToken.None"/> so one caller
    /// cancelling never poisons it for the others — each caller abandons only its OWN wait via
    /// <c>Task.WaitAsync</c>. A failed resolution is evicted so it never poisons the cache; a later call
    /// retries.
    /// </summary>
    /// <exception cref="SandboxException">
    /// <see cref="SandboxErrorKind.NotFound"/> when the session is missing or owned by a different
    /// app; <see cref="SandboxErrorKind.Protocol"/> when the gateway reports no workspace mount id
    /// (the direct file/command APIs require a writable workspace mount).
    /// </exception>
    internal async Task<long> ResolveWorkspaceMountIdAsync(string sessionId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var lazy = _workspaceMountIds.GetOrAdd(
            sessionId,
            static (id, self) => new Lazy<Task<long>>(() => self.FetchWorkspaceMountIdAsync(id, CancellationToken.None)),
            this
        );

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The CALLER abandoned its own wait; the shared fetch may still complete for other waiters, so
            // leave the entry in place rather than forcing a concurrent/subsequent caller to re-issue it.
            throw;
        }
        catch
        {
            // The shared resolution FAILED — drop the entry so a later call retries instead of replaying the
            // cached fault. Remove only the exact Lazy we observed, so a concurrent success/reseed for the
            // same session is never clobbered.
            _ = ((ICollection<KeyValuePair<string, Lazy<Task<long>>>>)_workspaceMountIds).Remove(
                new KeyValuePair<string, Lazy<Task<long>>>(sessionId, lazy)
            );
            throw;
        }
    }

    /// <summary>Issues the one <c>GET /api/v1/sandboxes/{session_id}</c> that resolves the workspace mount id (<c>volumes.workspace.id</c>).</summary>
    private async Task<long> FetchWorkspaceMountIdAsync(string sessionId, CancellationToken ct)
    {
        var operation = $"resolving the workspace mount id for sandbox '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, operation);
        }

        var payload = await ReadSandboxResponseOrThrowAsync(response, operation, ct).ConfigureAwait(false);
        return payload.Volumes?.Workspace?.Id
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway did not report a workspace mount id for sandbox '{sessionId}'; the "
                    + "direct file and command APIs require a writable workspace mount."
            );
    }

    /// <summary>
    /// Drops <paramref name="sessionId"/>'s cached workspace mount id, if any. Called after a successful
    /// <see cref="DeleteAsync"/> (the session — and its mount — no longer exist) and whenever the gateway
    /// reports a definitive <c>session_not_found</c> on a direct call, so a stale mapping can never be
    /// replayed against a session that has gone away. A no-op when nothing is cached for the id.
    /// </summary>
    internal void EvictWorkspaceMountId(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = _workspaceMountIds.TryRemove(sessionId, out _);
        }
    }
}
