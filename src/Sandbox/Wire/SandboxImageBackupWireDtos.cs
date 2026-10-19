using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.Sandbox.Wire;

// --- Client image (ADR 0039) and workspace backup (issue #191) REST contracts ---
// Verified against SandboxedOstoolsMcpServer origin/master 7f19413 (v0.1.16):
// crates/mcp-gateway/src/image_policy.rs (ImageReport), src/api/backup.rs, src/backup/service.rs
// (SnapshotInfo) and src/db/backup_store.rs (CostTotal).

/// <summary>
/// The injected runtime's conformance report on a sandbox image. Every field but <c>warnings</c> is
/// non-optional on the gateway; nullable here so a missing one is detected rather than defaulted.
/// </summary>
internal sealed record ImageReportDto(
    [property: JsonPropertyName("os_id")] string? OsId,
    [property: JsonPropertyName("debian_family")] bool? DebianFamily,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("uid")] long? Uid,
    [property: JsonPropertyName("gid")] long? Gid,
    [property: JsonPropertyName("user_reused")] bool? UserReused,
    [property: JsonPropertyName("has_bash")] bool? HasBash,
    [property: JsonPropertyName("has_pwsh")] bool? HasPwsh,
    [property: JsonPropertyName("has_system_ca_bundle")] bool? HasSystemCaBundle,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string?>? Warnings
);

/// <summary><c>GET /api/v1/backup/snapshots</c> response.</summary>
internal sealed record SnapshotListResponseDto(
    [property: JsonPropertyName("workspace")] string? Workspace,
    [property: JsonPropertyName("snapshots")] IReadOnlyList<SnapshotDto?>? Snapshots
);

internal sealed record SnapshotDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("orphan")] bool Orphan,
    [property: JsonPropertyName("tags")] IReadOnlyList<string?>? Tags,
    [property: JsonPropertyName("files")] long? Files,
    [property: JsonPropertyName("bytes")] long? Bytes
);

/// <summary>
/// <c>POST /api/v1/backup/restore</c> body. <c>app_id</c> is read by the gateway only when
/// <c>AUTH_ENFORCE</c> is off; under enforcement the authenticated app wins and it is ignored.
/// </summary>
internal sealed record RestoreRequestDto(
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("app_id")] string AppId
);

/// <summary>
/// <c>POST /api/v1/backup/restore</c> response. <c>restored</c> is <c>null</c> (and <c>orphan</c> absent)
/// when the local copy was already current and nothing was restored.
/// </summary>
internal sealed record RestoreResponseDto(
    [property: JsonPropertyName("workspace")] string? Workspace,
    [property: JsonPropertyName("restored")] string? Restored,
    [property: JsonPropertyName("orphan")] string? Orphan
);

/// <summary><c>DELETE /api/v1/backup/workspace</c> response.</summary>
internal sealed record PurgeResponseDto(
    [property: JsonPropertyName("workspace")] string? Workspace,
    [property: JsonPropertyName("snapshots_deleted")] long? SnapshotsDeleted,
    [property: JsonPropertyName("sandboxes_deleted")] IReadOnlyList<string?>? SandboxesDeleted
);

/// <summary><c>GET /api/v1/backup/costs</c> response.</summary>
internal sealed record CostsResponseDto(
    [property: JsonPropertyName("app_id")] string? AppId,
    [property: JsonPropertyName("since")] string? Since,
    [property: JsonPropertyName("group_by")] string? GroupBy,
    [property: JsonPropertyName("total_est_cost_usd")] double TotalEstCostUsd,
    [property: JsonPropertyName("total_storage_usd_per_month_added")] double TotalStorageUsdPerMonthAdded,
    [property: JsonPropertyName("groups")] IReadOnlyList<CostTotalDto?>? Groups
);

/// <summary>One cost group; <c>group</c> is <c>null</c> for app-level runs (init, prune).</summary>
internal sealed record CostTotalDto(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("runs")] long Runs,
    [property: JsonPropertyName("failed_runs")] long FailedRuns,
    [property: JsonPropertyName("est_cost_usd")] double EstCostUsd,
    [property: JsonPropertyName("storage_usd_per_month")] double StorageUsdPerMonth,
    [property: JsonPropertyName("puts")] long Puts,
    [property: JsonPropertyName("gets")] long Gets,
    [property: JsonPropertyName("bytes_up")] long BytesUp,
    [property: JsonPropertyName("bytes_down")] long BytesDown,
    [property: JsonPropertyName("last_run_at")] string? LastRunAt
);
