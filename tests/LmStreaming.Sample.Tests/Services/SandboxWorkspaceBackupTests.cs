using System.Net;
using System.Text;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The inspector's backup seam over a fake gateway: when the conversation's sandboxes are released relative to
/// the restore and purge calls the gateway sees.
/// </summary>
public class SandboxWorkspaceBackupTests
{
    private const string SnapshotsJson = """
        {"workspace":"ws","snapshots":[{"id":"snap-1","time":"2026-10-09T21:59:03.31947084+00:00[UTC]",
          "complete":true,"partial":false,"orphan":false,"tags":[],"files":5,"bytes":12702}]}
        """;

    [Fact]
    public async Task RestoreAsync_UnknownSnapshot_ReturnsNullWithoutReleasingTheLiveSandboxes()
    {
        var (backup, requests) = Create();
        var released = 0;

        var result = await backup.RestoreAsync(
            "ws",
            "no-such-snapshot",
            _ =>
            {
                released++;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result.Should().BeNull();
        released.Should().Be(0, "a mistyped or stale id must not cost anyone their live sandbox");
        requests.Should().NotContain(r => r.EndsWith("/api/v1/backup/restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreAsync_KnownSnapshot_ReleasesTheSandboxesBeforeTheGatewayRestore()
    {
        var (backup, requests) = Create();

        var result = await backup.RestoreAsync(
            "ws",
            "snap-1",
            _ =>
            {
                requests.Add("release");
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result!.RestoredSnapshotId.Should().Be("snap-1");
        requests
            .Where(r => r == "release" || r.StartsWith("POST", StringComparison.Ordinal))
            .Should()
            .Equal("release", "POST /api/v1/backup/restore");
    }

    [Fact]
    public async Task PurgeAsync_PurgesBeforeReleasing_SoTheGatewayReportsTheSandboxesItDeleted()
    {
        var (backup, requests) = Create();

        var result = await backup.PurgeAsync(
            "ws",
            _ =>
            {
                requests.Add("release");
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result.SandboxesDeleted.Should().Equal("s-1");
        requests.Should().Equal("DELETE /api/v1/backup/workspace", "release");
    }

    private static (SandboxWorkspaceBackup Backup, List<string> Requests) Create()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/backup/snapshots" => Json(SnapshotsJson),
                "/api/v1/backup/restore" => Json("""{"workspace":"ws","restored":"snap-1"}"""),
                "/api/v1/backup/workspace" => Json(
                    """{"workspace":"ws","snapshots_deleted":1,"sandboxes_deleted":["s-1"]}"""
                ),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var backup = new SandboxWorkspaceBackup(
            new SandboxGatewayOptions { AppId = "app-1" },
            () => "http://localhost:3000",
            new Mock<IWorkspaceStore>(MockBehavior.Strict).Object,
            new HttpClient(handler)
        );
        return (backup, requests);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
