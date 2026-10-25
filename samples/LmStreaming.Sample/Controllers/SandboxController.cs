using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// The conversation inspector's sandbox panel: what the conversation's live sandbox runs on (the image, digest
/// and conformance warnings the gateway acknowledged on create, and its home), plus the workspace's backups
/// (snapshots, restore, costs, purge). Authorized like the Mini App routes: an interactive end user with write
/// access to the conversation, refused (403) when a server-to-server caller established the conversation's
/// sandbox. Restore and purge also need the operator secret (<see cref="OperatorSecretAuthAttribute"/>): workspaces
/// have no owner, so without it any user bound to a workspace could wipe it for everyone. Never creates a sandbox.
/// </summary>
[ApiController]
[Route("api/conversations/{threadId}/sandbox")]
public sealed class SandboxController(
    SandboxAppAccess access,
    SandboxSessionRegistry registry,
    SandboxWorkspaceBackup backup,
    ConversationAuthorizer authorizer,
    IConversationStore conversations,
    ILogger<SandboxController> logger
) : ControllerBase
{
    public sealed record RestoreRequest(string SnapshotId);

    public sealed record PurgeRequest(string ConfirmWorkspace);

    [HttpGet]
    public async Task<IActionResult> Get(string threadId, CancellationToken ct)
    {
        var target = await ResolveAsync(threadId, ct);
        if (target.Failure is { } failure)
            return failure;

        // Image fields come from the create acknowledgement of a session this process created. A null image means
        // the gateway did not report one (an older gateway), so the sandbox runs the server default.
        // The thread's own slot: a read-only-egress thread (a judge) runs in a sandbox of its own, never the
        // normal one its workspace's other threads share.
        var readOnlyEgress = await ConversationReadOnlyEgress.ReadAsync(conversations, threadId, ct);
        var session = registry.TryGetExistingSession(target.WorkspaceId!, readOnlyEgress);
        return Ok(
            new
            {
                workspaceId = target.WorkspaceId,
                gatewayWorkspace = target.GatewayWorkspace,
                session = session is null
                    ? null
                    : new
                    {
                        sessionId = session.SessionId,
                        image = session.Image,
                        imageDigest = session.ImageDigest,
                        imageWarnings = session.ImageReport?.Warnings ?? [],
                        imageReport = session.ImageReport,
                        workingDir = session.WorkspaceWorkingDir,
                    },
            }
        );
    }

    [HttpGet("backup/snapshots")]
    public Task<IActionResult> Snapshots(string threadId, CancellationToken ct) =>
        RunAsync(
            threadId,
            "list",
            async (target, token) => Ok(await backup.ListSnapshotsAsync(target.GatewayWorkspace, token)),
            ct
        );

    /// <summary>
    /// Restores a snapshot (404 when the workspace has no such snapshot, before anything is stopped). The gateway
    /// refuses while any sandbox mounts the workspace, so this deletes the workspace's sandboxes first: every
    /// conversation on this workspace loses its live sandbox and its next turn creates a fresh one on the restored
    /// files. The client confirms that with the user before calling.
    /// </summary>
    [HttpPost("backup/restore")]
    [OperatorSecretAuth(RefuseWithForbidden = true)]
    public Task<IActionResult> Restore(string threadId, [FromBody] RestoreRequest request, CancellationToken ct) =>
        RunAsync(
            threadId,
            "restore",
            async (target, token) =>
            {
                if (string.IsNullOrWhiteSpace(request.SnapshotId))
                    return BadRequest(new { error = "snapshotId is required." });
                var restored = await backup.RestoreAsync(
                    target.GatewayWorkspace,
                    request.SnapshotId,
                    release => registry.ReleaseWorkspaceSessionsAsync(target.WorkspaceId, backup.AppId, release),
                    token
                );
                return restored is null
                    ? NotFound(
                        new { error = $"Workspace '{target.GatewayWorkspace}' has no snapshot '{request.SnapshotId}'." }
                    )
                    : Ok(restored);
            },
            ct
        );

    /// <summary>
    /// The backup costs of THIS conversation's workspace only. The gateway's report covers every workspace under
    /// the app, and conversation access says nothing about the others, so the groups and totals are scoped to it.
    /// </summary>
    [HttpGet("backup/costs")]
    public Task<IActionResult> Costs(string threadId, CancellationToken ct) =>
        RunAsync(
            threadId,
            "costs",
            async (target, token) => Ok(await backup.GetCostsAsync(target.GatewayWorkspace, token)),
            ct
        );

    /// <summary>
    /// PERMANENTLY purges the workspace: its sandboxes, snapshots and files. Requires the caller to echo the gateway
    /// workspace name, so a stray request cannot purge.
    /// </summary>
    [HttpPost("backup/purge")]
    [OperatorSecretAuth(RefuseWithForbidden = true)]
    public Task<IActionResult> Purge(string threadId, [FromBody] PurgeRequest request, CancellationToken ct) =>
        RunAsync(
            threadId,
            "purge",
            async (target, token) =>
            {
                if (!string.Equals(request.ConfirmWorkspace, target.GatewayWorkspace, StringComparison.Ordinal))
                    return BadRequest(
                        new { error = $"Type the workspace name '{target.GatewayWorkspace}' to confirm the purge." }
                    );
                return Ok(
                    await backup.PurgeAsync(
                        target.GatewayWorkspace,
                        release => registry.DestroyWorkspaceSessionAsync(target.WorkspaceId, backup.AppId, release),
                        token
                    )
                );
            },
            ct
        );

    private sealed record BackupTarget(string WorkspaceId, string GatewayWorkspace);

    private async Task<IActionResult> RunAsync(
        string threadId,
        string action,
        Func<BackupTarget, CancellationToken, Task<IActionResult>> run,
        CancellationToken ct
    )
    {
        var target = await ResolveAsync(threadId, ct);
        if (target.Failure is { } failure)
            return failure;
        try
        {
            return await run(new BackupTarget(target.WorkspaceId!, target.GatewayWorkspace!), ct);
        }
        catch (SandboxException ex)
        {
            logger.LogWarning(
                "Sandbox backup {Action} failed for workspace {GatewayWorkspace}: {Kind} {StatusCode} {ErrorCode}",
                action,
                target.GatewayWorkspace,
                ex.Kind,
                ex.StatusCode,
                ex.ErrorCode
            );
            var status = ex.Kind switch
            {
                SandboxErrorKind.NotFound => StatusCodes.Status404NotFound,
                SandboxErrorKind.Conflict => StatusCodes.Status409Conflict,
                SandboxErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
                SandboxErrorKind.Protocol when ex.StatusCode is >= 400 and < 500 => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(
                status,
                new
                {
                    error = ex.Message,
                    kind = ex.Kind.ToString(),
                    errorCode = ex.ErrorCode,
                    retryable = ex.Retryable,
                }
            );
        }
    }

    private async Task<(IActionResult? Failure, string? WorkspaceId, string? GatewayWorkspace)> ResolveAsync(
        string threadId,
        CancellationToken ct
    )
    {
        var (status, workspaceId) = await access.ResolveWorkspaceAsync(threadId, authorizer.Current, ct);
        if (status != StatusCodes.Status200OK || workspaceId is null)
            return (StatusCode(status), null, null);
        // The Mini App routes' cross-actor guard (CredentialConflict): an interactive caller may not act on a
        // conversation whose sandbox an S2S caller established.
        if (registry.TryGetEstablishedBinding(threadId, out var binding) && binding?.CallerCredential is not null)
            return (StatusCode(StatusCodes.Status403Forbidden), null, null);
        var gatewayWorkspace = await backup.ResolveGatewayWorkspaceAsync(workspaceId, ct);
        return gatewayWorkspace is null
            ? (Conflict(new { error = "No sandbox workspace is configured for this conversation." }), null, null)
            : (null, workspaceId, gatewayWorkspace);
    }
}
