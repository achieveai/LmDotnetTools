using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Workspace backup (gateway issue #191, <c>/api/v1/backup/*</c>). Every call is scoped to this client's
/// app: <c>workspace</c> is the same logical name a sandbox is created with
/// (<see cref="SandboxCreateRequest.Workspace"/>) and resolves under the app's own directory, so an app can
/// only reach its own backups.
/// </summary>
/// <remarks>
/// <para>
/// Every request also sends <c>app_id</c> = <see cref="SandboxClientOptions.AppId"/>. With the gateway's
/// <c>AUTH_ENFORCE</c> on, the authenticated app wins and the parameter is ignored; with it off (local
/// development) the gateway REQUIRES it (<c>400 app_id_required</c>). Sending it always works in both modes,
/// exactly as the create request always sends <c>app.id</c>.
/// </para>
/// <para>
/// Failures: <c>404 backup_disabled</c> (the gateway runs without backups) is
/// <see cref="SandboxErrorKind.Unavailable"/> with <see cref="SandboxException.Retryable"/> <c>false</c>;
/// <c>503 backup_unavailable</c> (the store could not be reached) is <see cref="SandboxErrorKind.Unavailable"/>
/// and retryable; <c>409 workspace_in_use</c>/<c>workspace_attached</c> are <see cref="SandboxErrorKind.Conflict"/>
/// and retryable; <c>404 snapshot_not_found</c> is <see cref="SandboxErrorKind.NotFound"/>; the <c>400</c>
/// argument rejections (<c>invalid_workspace</c>, <c>invalid_app_id</c>, …) are
/// <see cref="SandboxErrorKind.Protocol"/>. <see cref="SandboxException.ErrorCode"/> always names the reason.
/// </para>
/// </remarks>
public sealed partial class SandboxClient
{
    /// <summary>Lists the backup snapshots of <paramref name="workspace"/>, as the gateway orders them.</summary>
    public async Task<IReadOnlyList<SandboxBackupSnapshot>> ListBackupSnapshotsAsync(
        string workspace,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var operation = $"listing backup snapshots of workspace '{workspace}'";
        var (listed, statusCode) = await SendBackupAsync<SnapshotListResponseDto>(
                HttpMethod.Get,
                $"api/v1/backup/snapshots?workspace={Uri.EscapeDataString(workspace)}&{AppIdQuery()}",
                body: null,
                operation,
                ct
            )
            .ConfigureAwait(false);

        return
        [
            .. SelectNonNullOrThrow(listed.Snapshots, static snapshot => snapshot, operation, statusCode)
                .Select(snapshot => ToSnapshot(snapshot, operation, statusCode)),
        ];
    }

    /// <summary>
    /// Restores <paramref name="snapshotId"/> into <paramref name="workspace"/>. The gateway refuses while any
    /// of its sandboxes still mounts the workspace (<c>409 workspace_attached</c>): delete them first, or wait
    /// until they go dormant. A diverged local copy is saved aside first, reported as
    /// <see cref="SandboxBackupRestoreResult.OrphanSnapshotId"/>.
    /// </summary>
    public async Task<SandboxBackupRestoreResult> RestoreBackupSnapshotAsync(
        string workspace,
        string snapshotId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        var operation = $"restoring snapshot '{snapshotId}' into workspace '{workspace}'";
        var (restored, _) = await SendBackupAsync<RestoreResponseDto>(
                HttpMethod.Post,
                "api/v1/backup/restore",
                new RestoreRequestDto(workspace, snapshotId, _options.AppId),
                operation,
                ct
            )
            .ConfigureAwait(false);

        return new SandboxBackupRestoreResult
        {
            Workspace = restored.Workspace ?? workspace,
            RestoredSnapshotId = restored.Restored,
            OrphanSnapshotId = restored.Orphan,
        };
    }

    /// <summary>
    /// PERMANENTLY purges <paramref name="workspace"/>: the gateway first DELETES every one of its own sandboxes
    /// mounted on that workspace (through the normal sandbox delete path), then deletes all of the workspace's
    /// snapshots and its local directory, and tombstones it so a sandbox on another gateway cannot revive it from
    /// backup (that revive fails with <see cref="SandboxErrorKind.WorkspacePurged"/>). None of this can be undone.
    /// A new create on the same workspace name on this gateway starts from an empty workspace (observed on v0.1.16). <see cref="SandboxBackupPurgeResult.SandboxesDeleted"/> names the sandboxes it deleted.
    /// </summary>
    public async Task<SandboxBackupPurgeResult> PurgeBackupWorkspaceAsync(
        string workspace,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        var operation = $"purging workspace '{workspace}'";
        var (purged, statusCode) = await SendBackupAsync<PurgeResponseDto>(
                HttpMethod.Delete,
                $"api/v1/backup/workspace?workspace={Uri.EscapeDataString(workspace)}&{AppIdQuery()}",
                body: null,
                operation,
                ct
            )
            .ConfigureAwait(false);

        var sandboxesDeleted = SelectNonNullOrThrow(purged.SandboxesDeleted, static id => id, operation, statusCode);
        foreach (var sessionId in sandboxesDeleted)
        {
            // Those sandboxes are gone: never replay their cached workspace mount ids.
            EvictWorkspaceMountId(sessionId);
        }

        return new SandboxBackupPurgeResult
        {
            Workspace = purged.Workspace ?? workspace,
            SnapshotsDeleted =
                purged.SnapshotsDeleted
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway did not report how many snapshots it deleted {operation}.",
                    statusCode
                ),
            SandboxesDeleted = sandboxesDeleted,
        };
    }

    /// <summary>
    /// Reads this app's estimated backup costs since <paramref name="since"/> (all time when <c>null</c>),
    /// grouped per sandbox or per workspace.
    /// </summary>
    public async Task<SandboxBackupCosts> GetBackupCostsAsync(
        DateTimeOffset? since = null,
        SandboxBackupCostGrouping groupBy = SandboxBackupCostGrouping.Sandbox,
        CancellationToken ct = default
    )
    {
        var group = groupBy switch
        {
            SandboxBackupCostGrouping.Sandbox => "sandbox",
            SandboxBackupCostGrouping.Workspace => "workspace",
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, "Unknown backup cost grouping."),
        };
        var query = $"group_by={group}&{AppIdQuery()}";
        if (since is { } bound)
        {
            // RFC 3339 in UTC, which the gateway parses and normalizes itself.
            query += "&since=" + Uri.EscapeDataString(bound.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        }

        const string operation = "reading backup costs";
        var (costs, statusCode) = await SendBackupAsync<CostsResponseDto>(
                HttpMethod.Get,
                $"api/v1/backup/costs?{query}",
                body: null,
                operation,
                ct
            )
            .ConfigureAwait(false);

        return new SandboxBackupCosts
        {
            AppId =
                costs.AppId
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "Sandbox gateway did not report the app id for backup costs.",
                    statusCode
                ),
            Since = costs.Since,
            GroupBy = costs.GroupBy ?? group,
            TotalEstimatedCostUsd = costs.TotalEstCostUsd,
            TotalStorageUsdPerMonthAdded = costs.TotalStorageUsdPerMonthAdded,
            Groups =
            [
                .. SelectNonNullOrThrow(costs.Groups, static row => row, operation, statusCode)
                    .Select(row => new SandboxBackupCostGroup
                    {
                        Group = row.Group,
                        Runs = row.Runs,
                        FailedRuns = row.FailedRuns,
                        EstimatedCostUsd = row.EstCostUsd,
                        StorageUsdPerMonth = row.StorageUsdPerMonth,
                        Puts = row.Puts,
                        Gets = row.Gets,
                        BytesUp = row.BytesUp,
                        BytesDown = row.BytesDown,
                        LastRunAt = row.LastRunAt,
                    }),
            ],
        };
    }

    private string AppIdQuery() => "app_id=" + Uri.EscapeDataString(_options.AppId);

    /// <summary>
    /// Sends one backup REST call and reads its JSON body, classifying a non-2xx through the shared gateway error
    /// table and a malformed or <c>null</c> body as <see cref="SandboxErrorKind.Protocol"/>.
    /// </summary>
    private async Task<(T Value, int StatusCode)> SendBackupAsync<T>(
        HttpMethod method,
        string relativeUri,
        object? body,
        string operation,
        CancellationToken ct
    )
        where T : class
    {
        using var response = await SendRestAsync(method, relativeUri, body, sessionId: null, ct).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        T? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<T>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed response {operation}.",
                statusCode,
                ex
            );
        }

        return payload is null
            ? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an empty response {operation}.",
                statusCode
            )
            : (payload, statusCode);
    }

    private static SandboxBackupSnapshot ToSnapshot(SnapshotDto dto, string operation, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Time))
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a snapshot with no id or time {operation}.",
                statusCode
            );
        }

        return new SandboxBackupSnapshot
        {
            Id = dto.Id,
            Time = dto.Time,
            Complete = dto.Complete,
            Partial = dto.Partial,
            Orphan = dto.Orphan,
            Tags = SelectNonNullOrThrow(dto.Tags, static tag => tag, operation, statusCode),
            Files = dto.Files,
            Bytes = dto.Bytes,
        };
    }
}
