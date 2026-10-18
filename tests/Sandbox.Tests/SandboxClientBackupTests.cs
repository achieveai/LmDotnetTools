using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Workspace backup (<c>/api/v1/backup/*</c>, gateway issue #191) against bodies captured from a live
/// v0.1.16 gateway and the error table in its <c>api/backup.rs</c>.
/// </summary>
public sealed class SandboxClientBackupTests
{
    [Fact]
    public async Task ListBackupSnapshotsAsync_SendsWorkspaceAndAppId_AndMapsTheSnapshots()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/backup/snapshots",
            """
            {"snapshots":[{"bytes":17,"complete":true,"files":1,"id":"57ec26a7","orphan":false,"partial":false,
              "tags":["gw:sbx-sdksync-3100","sbx:440c7316"],"time":"2026-10-09T20:20:34.646329723+00:00[UTC]"}],
             "workspace":"my ws"}
            """
        );

        var snapshots = await client.ListBackupSnapshotsAsync("my ws");

        var query = handler.Requests.Single().Uri.Query;
        query.Should().Contain("workspace=my%20ws").And.Contain("app_id=app-1");
        snapshots
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new SandboxBackupSnapshot
                {
                    Id = "57ec26a7",
                    Time = "2026-10-09T20:20:34.646329723+00:00[UTC]",
                    Complete = true,
                    Partial = false,
                    Orphan = false,
                    Tags = ["gw:sbx-sdksync-3100", "sbx:440c7316"],
                    Files = 1,
                    Bytes = 17,
                }
            );
    }

    [Theory]
    [InlineData("{\"workspace\":\"ws\",\"restored\":\"snap-1\",\"orphan\":\"snap-2\"}", "snap-1", "snap-2")]
    // The local copy was already current: restored null and no orphan field at all.
    [InlineData("{\"workspace\":\"ws\",\"restored\":null}", null, null)]
    public async Task RestoreBackupSnapshotAsync_PostsTheBody_AndMapsTheOutcome(
        string responseJson,
        string? restored,
        string? orphan
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(HttpMethod.Post, "/api/v1/backup/restore", responseJson);

        var result = await client.RestoreBackupSnapshotAsync("ws", "snap-1");

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.GetProperty("workspace").GetString().Should().Be("ws");
        sent.RootElement.GetProperty("snapshot_id").GetString().Should().Be("snap-1");
        sent.RootElement.GetProperty("app_id").GetString().Should().Be("app-1");
        result.RestoredSnapshotId.Should().Be(restored);
        result.OrphanSnapshotId.Should().Be(orphan);
    }

    [Fact]
    public async Task PurgeBackupWorkspaceAsync_SendsDelete_AndReportsTheDeletedSandboxes()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(
            HttpMethod.Delete,
            "/api/v1/backup/workspace",
            "{\"workspace\":\"ws\",\"snapshots_deleted\":3,\"sandboxes_deleted\":[\"s-1\"]}"
        );

        var result = await client.PurgeBackupWorkspaceAsync("ws");

        handler.Requests.Single().Uri.Query.Should().Contain("workspace=ws").And.Contain("app_id=app-1");
        result.SnapshotsDeleted.Should().Be(3);
        result.SandboxesDeleted.Should().Equal("s-1");
    }

    [Fact]
    public async Task GetBackupCostsAsync_SendsGroupingAndSince_AndMapsTheGroups()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(appId: "app-1");
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/backup/costs",
            """
            {"app_id":"app-1","group_by":"workspace","since":"2026-10-01T00:00:00.000Z",
             "groups":[{"bytes_down":0,"bytes_up":2018,"est_cost_usd":0.0000666,"failed_runs":0,"gets":4,
               "group":"app-1-7f7f/smoke","last_run_at":"2026-10-09T20:20:33.930Z","puts":6,"runs":2,
               "storage_usd_per_month":4.6414e-8},
               {"bytes_down":155,"bytes_up":0,"est_cost_usd":0.0000308,"failed_runs":0,"gets":2,"group":null,
               "last_run_at":null,"puts":1,"runs":1,"storage_usd_per_month":0.0}],
             "total_est_cost_usd":0.0000974,"total_storage_usd_per_month_added":4.6414e-8}
            """
        );

        var costs = await client.GetBackupCostsAsync(
            new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            SandboxBackupCostGrouping.Workspace
        );

        var query = Uri.UnescapeDataString(handler.Requests.Single().Uri.Query);
        query.Should().Contain("group_by=workspace").And.Contain("since=2026-10-01T00:00:00.000Z");
        query.Should().Contain("app_id=app-1");
        costs.TotalEstimatedCostUsd.Should().Be(0.0000974);
        costs.Groups.Should().HaveCount(2);
        costs.Groups[0].Group.Should().Be("app-1-7f7f/smoke");
        costs.Groups[0].BytesUp.Should().Be(2018);
        costs.Groups[1].Group.Should().BeNull("app-level runs have no group");
    }

    [Theory]
    // backup_disabled is a 404 that must NOT read as "the session is gone".
    [InlineData(404, "backup_disabled", false, SandboxErrorKind.Unavailable)]
    [InlineData(503, "backup_unavailable", true, SandboxErrorKind.Unavailable)]
    [InlineData(404, "snapshot_not_found", false, SandboxErrorKind.NotFound)]
    [InlineData(409, "workspace_attached", true, SandboxErrorKind.Conflict)]
    [InlineData(409, "workspace_in_use", true, SandboxErrorKind.Conflict)]
    [InlineData(400, "invalid_workspace", false, SandboxErrorKind.Protocol)]
    [InlineData(400, "app_id_required", false, SandboxErrorKind.Protocol)]
    public async Task RestoreBackupSnapshotAsync_GatewayErrorCode_ClassifiesByCode(
        int status,
        string errorCode,
        bool retryable,
        SandboxErrorKind expected
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/backup/restore",
            $$"""{"error":"free text","code":{{status}},"error_code":"{{errorCode}}","retryable":{{(retryable ? "true" : "false")}}}""",
            (HttpStatusCode)status
        );

        var act = () => client.RestoreBackupSnapshotAsync("ws", "snap-1");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(expected);
        thrown.ErrorCode.Should().Be(errorCode);
        thrown.Retryable.Should().Be(retryable);
    }
}
