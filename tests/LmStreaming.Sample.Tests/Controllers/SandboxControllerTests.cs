using System.Collections.Immutable;
using System.Net;
using System.Text;
using LmStreaming.Sample.SandboxApps;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
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

    private sealed class Fixture : IAsyncDisposable
    {
        public required Harness Harness { get; init; }
        public required Workspace Workspace { get; init; }
        public required SandboxController Controller { get; init; }
        public required List<string> BackupRequests { get; init; }
        public required SandboxWorkspaceBackup Backup { get; init; }

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
            var controller = new SandboxController(
                new SandboxAppAccess(conversations.Object, Mock.Of<IWorkspaceFileBrowser>(), authorizer),
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
