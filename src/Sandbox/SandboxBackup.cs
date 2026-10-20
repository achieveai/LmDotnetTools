namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>One backup snapshot of a workspace, as <see cref="SandboxClient.ListBackupSnapshotsAsync(string, CancellationToken)"/> reports it.</summary>
public sealed record SandboxBackupSnapshot
{
    /// <summary>The snapshot id; pass it to <see cref="SandboxClient.RestoreBackupSnapshotAsync(string, string, CancellationToken)"/>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// When the snapshot was taken, exactly as the gateway reported it. Kept as text: the gateway's
    /// timestamps carry nanoseconds and a zone suffix (<c>…+00:00[UTC]</c>) that
    /// <see cref="DateTimeOffset"/> does not parse.
    /// </summary>
    public required string Time { get; init; }

    /// <summary>Whether the snapshot captured the whole workspace.</summary>
    public required bool Complete { get; init; }

    /// <summary>Whether the snapshot is partial (an interrupted backup). Never restored automatically.</summary>
    public required bool Partial { get; init; }

    /// <summary>Whether the snapshot holds a diverged local copy saved aside before a restore. Never restored automatically.</summary>
    public required bool Orphan { get; init; }

    /// <summary>The snapshot's tags (for example <c>gw:&lt;gateway&gt;</c>, <c>sbx:&lt;session&gt;</c>).</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>File count, when the gateway reported it.</summary>
    public long? Files { get; init; }

    /// <summary>Total bytes, when the gateway reported it.</summary>
    public long? Bytes { get; init; }
}

/// <summary>Outcome of <see cref="SandboxClient.RestoreBackupSnapshotAsync(string, string, CancellationToken)"/>.</summary>
public sealed record SandboxBackupRestoreResult
{
    /// <summary>The workspace the restore ran on.</summary>
    public required string Workspace { get; init; }

    /// <summary>
    /// The snapshot that was restored, or <see langword="null"/> when the gateway restored nothing because
    /// the local copy was already current.
    /// </summary>
    public string? RestoredSnapshotId { get; init; }

    /// <summary>
    /// The snapshot the gateway saved the diverged local copy into before restoring, when it had to;
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? OrphanSnapshotId { get; init; }
}

/// <summary>Outcome of <see cref="SandboxClient.PurgeBackupWorkspaceAsync(string, CancellationToken)"/>.</summary>
public sealed record SandboxBackupPurgeResult
{
    /// <summary>The purged workspace.</summary>
    public required string Workspace { get; init; }

    /// <summary>How many snapshots were deleted.</summary>
    public required long SnapshotsDeleted { get; init; }

    /// <summary>The session ids of this gateway's sandboxes that were deleted because they used the workspace.</summary>
    public required IReadOnlyList<string> SandboxesDeleted { get; init; }
}

/// <summary>How <see cref="SandboxClient.GetBackupCostsAsync"/> groups its totals.</summary>
public enum SandboxBackupCostGrouping
{
    /// <summary>One group per sandbox session (the gateway default).</summary>
    Sandbox,

    /// <summary>One group per workspace.</summary>
    Workspace,
}

/// <summary>Estimated backup storage cost for the calling app, from <see cref="SandboxClient.GetBackupCostsAsync"/>.</summary>
public sealed record SandboxBackupCosts
{
    /// <summary>The app the costs belong to.</summary>
    public required string AppId { get; init; }

    /// <summary>The normalized lower bound the gateway applied, or <see langword="null"/> for "all time".</summary>
    public string? Since { get; init; }

    /// <summary>The grouping the gateway applied (<c>sandbox</c> or <c>workspace</c>).</summary>
    public required string GroupBy { get; init; }

    /// <summary>Sum of <see cref="SandboxBackupCostGroup.EstimatedCostUsd"/> over every group.</summary>
    public required double TotalEstimatedCostUsd { get; init; }

    /// <summary>Sum of <see cref="SandboxBackupCostGroup.StorageUsdPerMonth"/> over every group.</summary>
    public required double TotalStorageUsdPerMonthAdded { get; init; }

    /// <summary>The per-group totals.</summary>
    public required IReadOnlyList<SandboxBackupCostGroup> Groups { get; init; }
}

/// <summary>Backup cost totals for one sandbox or workspace.</summary>
public sealed record SandboxBackupCostGroup
{
    /// <summary>The sandbox session id or workspace key; <see langword="null"/> for app-level runs (init, prune).</summary>
    public string? Group { get; init; }

    /// <summary>Backup and restore runs counted.</summary>
    public required long Runs { get; init; }

    /// <summary>Runs that failed.</summary>
    public required long FailedRuns { get; init; }

    /// <summary>Estimated request and transfer cost in USD.</summary>
    public required double EstimatedCostUsd { get; init; }

    /// <summary>Estimated storage added, in USD per month.</summary>
    public required double StorageUsdPerMonth { get; init; }

    /// <summary>Object-store PUT requests.</summary>
    public required long Puts { get; init; }

    /// <summary>Object-store GET requests.</summary>
    public required long Gets { get; init; }

    /// <summary>Bytes uploaded.</summary>
    public required long BytesUp { get; init; }

    /// <summary>Bytes downloaded.</summary>
    public required long BytesDown { get; init; }

    /// <summary>When the last run started, as the gateway reported it.</summary>
    public string? LastRunAt { get; init; }
}
