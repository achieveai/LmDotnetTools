using System.Collections.Immutable;
using System.Net;
using System.Text;
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

        public static async Task<Fixture> CreateAsync(bool restoreConflict = false)
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
                new HttpClient(new BackupGateway(requests, workspace.DirectoryRelPath, restoreConflict))
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

    private sealed class BackupGateway(List<string> requests, string workspace, bool restoreConflict)
        : HttpMessageHandler
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
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                }
            );
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
