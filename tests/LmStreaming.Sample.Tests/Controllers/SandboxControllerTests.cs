using System.Collections.Immutable;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static LmStreaming.Sample.Tests.Services.WorkspacePluginSelectionServiceTests;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// The inspector's destructive backup routes against a real registry and a stub backup gateway: the guards that
/// run before anything is stopped or purged, and what a restore the gateway refuses leaves behind.
/// </summary>
public class SandboxControllerTests
{
    private const string ThreadId = "thread-1";
    private const string BackupAppId = "app-a";

    [Fact]
    public async Task Purge_WrongConfirmation_Is400_AndNothingReachesTheGateway()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller.Purge(
            ThreadId,
            new SandboxController.PurgeRequest("not-the-workspace"),
            CancellationToken.None
        );

        result.Should().BeOfType<BadRequestObjectResult>();
        fixture.BackupRequests.Should().BeEmpty();
        fixture.Harness.Gateway.DeletedSessionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_BlankSnapshotId_Is400_AndNothingIsStopped()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Controller.Restore(
            ThreadId,
            new SandboxController.RestoreRequest("  "),
            CancellationToken.None
        );

        result.Should().BeOfType<BadRequestObjectResult>();
        fixture.BackupRequests.Should().BeEmpty();
        fixture.Harness.Gateway.DeletedSessionIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("purge")]
    public async Task BackupRoutes_ConversationEstablishedByAnS2SCaller_Are403_AndStopNothing(string route)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Harness.Registry.PublishEstablishedBinding(
            ThreadId,
            new SandboxEstablishedBinding(
                new WorkspaceRef(fixture.Workspace.Id),
                fixture.Harness.Registry.DefaultCredential,
                CredentialFor("s2s-app")
            )
        );

        var result =
            route == "restore"
                ? await fixture.Controller.Restore(
                    ThreadId,
                    new SandboxController.RestoreRequest("snap-1"),
                    CancellationToken.None
                )
                : await fixture.Controller.Purge(
                    ThreadId,
                    new SandboxController.PurgeRequest(fixture.Workspace.DirectoryRelPath),
                    CancellationToken.None
                );

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        fixture.BackupRequests.Should().BeEmpty();
        fixture.Harness.Gateway.DeletedSessionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Restore_GatewayStillSeesAnAttachedSandbox_Is409_SaysSandboxesStopped_AndSparesOtherApps()
    {
        await using var fixture = await Fixture.CreateAsync(restoreConflict: true);
        var mine = await SeedSessionAsync(fixture.Harness, fixture.Workspace, BackupAppId);
        var theirs = await SeedSessionAsync(fixture.Harness, fixture.Workspace, "app-b");

        var result = await fixture.Controller.Restore(
            ThreadId,
            new SandboxController.RestoreRequest("snap-1"),
            CancellationToken.None
        );

        var conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        conflict.Value.Should().BeEquivalentTo(new { errorCode = "workspace_attached", retryable = true });
        JsonSerializer.Serialize(conflict.Value).Should().Contain("live sandboxes were stopped");
        fixture
            .Harness.Gateway.DeletedSessionIds.Should()
            .Equal([mine.SessionId], "the gateway only refuses over the backup app's own sandboxes");
        fixture.Harness.Registry.TryGetSessionById(theirs.SessionId, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("purge")]
    public async Task BackupRoutes_WithoutTheRightOperatorSecret_Are403_AndStopNothing(string route)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await SeedSessionAsync(fixture.Harness, fixture.Workspace, BackupAppId);
        using var host = await StartAsync(fixture, OperatorGateTestServer.Secret);

        foreach (var presented in new[] { null, "not-the-secret" })
        {
            // 403, not 401: the SPA reads a 401 on /api as its own sign-in expiring.
            var response = await SendAsync(host, fixture, route, presented);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        fixture.BackupRequests.Should().BeEmpty();
        fixture.Harness.Gateway.DeletedSessionIds.Should().BeEmpty();
        fixture.Harness.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("purge")]
    public async Task BackupRoutes_OperatorSecretUnconfigured_Are503_ButSnapshotsStillList(string route)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var host = await StartAsync(fixture, secret: null);

        var refused = await SendAsync(host, fixture, route, "anything");
        var snapshots = await OperatorGateTestServer.SendAsync(
            host,
            HttpMethod.Get,
            $"/api/conversations/{ThreadId}/sandbox/backup/snapshots",
            presentedSecret: null
        );

        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        snapshots.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.BackupRequests.Should().Equal(["GET /api/v1/backup/snapshots"]);
    }

    [Fact]
    public async Task Restore_WithTheOperatorSecret_ReachesTheGateway()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var host = await StartAsync(fixture, OperatorGateTestServer.Secret);

        var response = await SendAsync(host, fixture, "restore", OperatorGateTestServer.Secret);

        // The stub gateway knows snap-1 but has no restore route, so the restore itself fails; the point is that
        // the request got past the operator gate and reached the gateway.
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        fixture.BackupRequests.Should().Contain("POST /api/v1/backup/restore");
    }

    [Fact]
    public async Task Restore_SandboxDeleteFails_Is503_RestoresNothing_AndARetryStopsTheSameSandboxThenRestores()
    {
        await using var fixture = await Fixture.CreateAsync(restoreSucceeds: true);
        var session = await SeedSessionAsync(fixture.Harness, fixture.Workspace, BackupAppId);
        fixture.Harness.Gateway.FailNextDeletes(1);

        var first = await fixture.Controller.Restore(
            ThreadId,
            new SandboxController.RestoreRequest("snap-1"),
            CancellationToken.None
        );

        var refused = first.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        JsonSerializer.Serialize(refused.Value).Should().Contain("Nothing was restored");
        fixture
            .BackupRequests.Should()
            .NotContain("POST /api/v1/backup/restore", "the sandbox may still mount the workspace");
        fixture.Harness.Gateway.DeletedSessionIds.Should().BeEmpty();
        fixture
            .Harness.Registry.TryGetSessionById(session.SessionId, out _)
            .Should()
            .BeTrue("a sandbox whose delete failed must stay tracked, or a retry has nothing to stop");

        var retry = await fixture.Controller.Restore(
            ThreadId,
            new SandboxController.RestoreRequest("snap-1"),
            CancellationToken.None
        );

        retry.Should().BeOfType<OkObjectResult>();
        fixture.Harness.Gateway.DeletedSessionIds.Should().Equal([session.SessionId]);
        fixture.BackupRequests.Count(r => r == "POST /api/v1/backup/restore").Should().Be(1);
        fixture.Harness.Registry.TryGetSessionById(session.SessionId, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Costs_ReportOnlyTheConversationsWorkspace_WithTotalsFromItsGroup(bool workspaceHasRuns)
    {
        await using var fixture = await Fixture.CreateAsync(costsIncludeOwnWorkspace: workspaceHasRuns);

        var result = await fixture.Controller.Costs(ThreadId, CancellationToken.None);

        var costs = result
            .Should()
            .BeOfType<OkObjectResult>()
            .Subject.Value.Should()
            .BeOfType<SandboxBackupCosts>()
            .Subject;
        var json = JsonSerializer.Serialize(costs);
        json.Should().NotContain("other-ws", "another workspace's usage is not this conversation's to read");
        json.Should().NotContain("prefix-", "a workspace whose name merely ends with this one is another workspace");
        if (workspaceHasRuns)
        {
            var own = costs.Groups.Should().ContainSingle().Subject;
            own.Group.Should().Be($"app-a-7f7f/{fixture.Workspace.DirectoryRelPath}");
            costs.TotalEstimatedCostUsd.Should().Be(own.EstimatedCostUsd);
            costs.TotalStorageUsdPerMonthAdded.Should().Be(own.StorageUsdPerMonth);
        }
        else
        {
            costs.Groups.Should().BeEmpty();
            costs.TotalEstimatedCostUsd.Should().Be(0);
            costs.TotalStorageUsdPerMonthAdded.Should().Be(0);
        }
        fixture.BackupRequests.Should().Equal(["GET /api/v1/backup/costs"]);
    }

    private static Task<IHost> StartAsync(Fixture fixture, string? secret) =>
        OperatorGateTestServer.StartAsync(
            secret,
            services =>
            {
                _ = services.AddSingleton(fixture.Access);
                _ = services.AddSingleton(fixture.Harness.Registry);
                _ = services.AddSingleton(fixture.Backup);
                _ = services.AddSingleton(fixture.Authorizer);
                _ = services.AddLogging();
            }
        );

    private static Task<HttpResponseMessage> SendAsync(IHost host, Fixture fixture, string route, string? secret) =>
        OperatorGateTestServer.SendAsync(
            host,
            HttpMethod.Post,
            $"/api/conversations/{ThreadId}/sandbox/backup/{route}",
            secret,
            route == "restore"
                ? new SandboxController.RestoreRequest("snap-1")
                : new SandboxController.PurgeRequest(fixture.Workspace.DirectoryRelPath)
        );

    private sealed class Fixture : IAsyncDisposable
    {
        public required Harness Harness { get; init; }
        public required Workspace Workspace { get; init; }
        public required SandboxController Controller { get; init; }
        public required List<string> BackupRequests { get; init; }
        public required SandboxWorkspaceBackup Backup { get; init; }
        public required SandboxAppAccess Access { get; init; }
        public required ConversationAuthorizer Authorizer { get; init; }

        public static async Task<Fixture> CreateAsync(
            bool restoreConflict = false,
            bool restoreSucceeds = false,
            bool costsIncludeOwnWorkspace = true
        )
        {
            var harness = Harness.Create();
            var workspace = await SeedWorkspaceAsync(harness, ["official"]);
            var conversations = new Mock<IConversationStore>();
            conversations
                .Setup(x => x.LoadMetadataAsync(ThreadId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    new ThreadMetadata
                    {
                        ThreadId = ThreadId,
                        LastUpdated = 0,
                        Properties = ImmutableDictionary<string, object>.Empty.Add(
                            MultiTurnAgentPool.WorkspacePropertyKey,
                            workspace.Id
                        ),
                    }
                );
            var requests = new List<string>();
            var backup = new SandboxWorkspaceBackup(
                new SandboxGatewayOptions { AppId = BackupAppId, BaseUrl = "http://localhost:3000" },
                harness.FileStore,
                new HttpClient(
                    new BackupGateway(
                        requests,
                        workspace.DirectoryRelPath,
                        restoreConflict,
                        restoreSucceeds,
                        costsIncludeOwnWorkspace
                    )
                )
            );
            var authorizer = TestAuthorizers.Disabled();
            var access = new SandboxAppAccess(conversations.Object, Mock.Of<IWorkspaceFileBrowser>(), authorizer);
            var controller = new SandboxController(
                access,
                harness.Registry,
                backup,
                authorizer,
                NullLogger<SandboxController>.Instance
            );
            return new Fixture
            {
                Harness = harness,
                Workspace = workspace,
                Controller = controller,
                BackupRequests = requests,
                Backup = backup,
                Access = access,
                Authorizer = authorizer,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Backup.Dispose();
            await Harness.DisposeAsync();
        }
    }

    private sealed class BackupGateway(
        List<string> requests,
        string workspace,
        bool restoreConflict,
        bool restoreSucceeds,
        bool costsIncludeOwnWorkspace
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(
                request.RequestUri.AbsolutePath switch
                {
                    "/api/v1/backup/snapshots" => Json(
                        HttpStatusCode.OK,
                        $$"""{"workspace":"{{workspace}}","snapshots":[{"id":"snap-1","time":"2026-10-09T21:59:03Z","complete":true,"partial":false,"orphan":false,"tags":[]}]}"""
                    ),
                    "/api/v1/backup/restore" when restoreConflict => Json(
                        HttpStatusCode.Conflict,
                        """{"error":"workspace is attached","code":409,"error_code":"workspace_attached","retryable":true}"""
                    ),
                    "/api/v1/backup/restore" when restoreSucceeds => Json(
                        HttpStatusCode.OK,
                        $$"""{"workspace":"{{workspace}}","restored":"snap-1"}"""
                    ),
                    // Workspace-grouped costs for the WHOLE app (gateway workspace_key = <app-dir>/<workspace>):
                    // this conversation's workspace, two others, and an app-level run with no workspace.
                    "/api/v1/backup/costs" => Json(HttpStatusCode.OK, CostsJson()),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                }
            );
        }

        private string CostsJson()
        {
            var own = costsIncludeOwnWorkspace
                ? $$"""{"group":"app-a-7f7f/{{workspace}}","runs":2,"failed_runs":0,"est_cost_usd":0.25,"storage_usd_per_month":0.5,"puts":6,"gets":4,"bytes_up":2018,"bytes_down":0,"last_run_at":"2026-10-09T20:20:33.930Z"},"""
                : string.Empty;
            return $$"""
                {"app_id":"app-a","group_by":"workspace","since":null,
                 "groups":[{{own}}
                   {"group":"app-a-7f7f/other-ws","runs":5,"failed_runs":1,"est_cost_usd":4.0,"storage_usd_per_month":8.0,"puts":9,"gets":9,"bytes_up":9999,"bytes_down":9,"last_run_at":"2026-10-08T00:00:00Z"},
                   {"group":"app-a-7f7f/prefix-{{workspace}}","runs":1,"failed_runs":0,"est_cost_usd":16.0,"storage_usd_per_month":32.0,"puts":1,"gets":1,"bytes_up":1,"bytes_down":1,"last_run_at":null},
                   {"group":null,"runs":1,"failed_runs":0,"est_cost_usd":64.0,"storage_usd_per_month":0.0,"puts":1,"gets":2,"bytes_up":0,"bytes_down":155,"last_run_at":null}],
                 "total_est_cost_usd":84.25,"total_storage_usd_per_month_added":40.5}
                """;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
