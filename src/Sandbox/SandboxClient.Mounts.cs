using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Caches each session's writable workspace mount id (<c>session_mounts.id</c>) — the integer
    /// every direct file/command API is keyed by. Populated lazily because the public command/file
    /// methods receive only a session id (a session may have been created by a different client or
    /// registry), so the mount id must be discoverable on demand; cached because it is stable for a
    /// session's lifetime (the gateway assigns it at create and never renumbers it).
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _workspaceMountIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Seeds <see cref="_workspaceMountIds"/> from a create/get result that already carries the
    /// workspace mount id, so the first <see cref="ExecuteAsync"/>/file call after a create does not
    /// pay a redundant <c>GET</c> to rediscover it. A no-op when the info has no mount id (e.g. a
    /// list result, which carries no volumes).
    /// </summary>
    private void SeedWorkspaceMountId(SandboxInfo info)
    {
        if (info.WorkspaceMountId is { } mountId)
        {
            _workspaceMountIds[info.SessionId] = mountId;
        }
    }

    /// <summary>
    /// Resolves the writable workspace mount id for <paramref name="sessionId"/>, returning the
    /// cached value when present or fetching it once via <c>GET /api/v1/sandboxes/{session_id}</c>
    /// (<c>volumes.workspace.id</c>). Every direct file/command call routes through here so a caller
    /// never has to know the mount id.
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

        if (_workspaceMountIds.TryGetValue(sessionId, out var cached))
        {
            return cached;
        }

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
        var mountId =
            payload.Volumes?.Workspace?.Id
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway did not report a workspace mount id for sandbox '{sessionId}'; the "
                    + "direct file and command APIs require a writable workspace mount."
            );

        _workspaceMountIds[sessionId] = mountId;
        return mountId;
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
