using System.Net;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Wire-level tests for <see cref="SandboxClient.ResolveWorkspaceMountIdAsync"/> — the lazy,
/// per-session cache of the workspace <c>session_mounts.id</c> that every direct file/command API is
/// keyed by. Verifies the create/get response seeds the cache, that a cold resolve issues exactly one
/// <c>GET</c> and then serves from cache, and the missing-workspace / missing-session failure shapes.
/// </summary>
public sealed class SandboxClientMountResolutionTests
{
    private const string SessionId = "s1";

    private static string CreateResponse(long? mountId, bool includeVolumes = true)
    {
        if (!includeVolumes)
        {
            return "{\"session_id\":\"" + SessionId + "\",\"container_id\":\"c1\"}";
        }

        var idField = mountId is { } id ? ",\"id\":" + id : string.Empty;
        return "{\"session_id\":\""
            + SessionId
            + "\",\"container_id\":\"c1\",\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false"
            + idField
            + "}}}";
    }

    private static bool IsGetSession(HttpRequestMessage req) =>
        req.Method == HttpMethod.Get
        && req.RequestUri!.AbsolutePath.EndsWith($"/sandboxes/{SessionId}", StringComparison.Ordinal);

    private static bool IsCreate(HttpRequestMessage req) =>
        req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/sandboxes", StringComparison.Ordinal);

    [Fact]
    public async Task Create_SeedsMountIdCache_SoResolveIssuesNoGet()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(IsCreate, _ => Json(CreateResponse(7)));

        var info = await client.CreateAsync(new SandboxCreateRequest(workspace: "ws"));
        info.WorkspaceMountId.Should().Be(7);

        var mountId = await client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);

        mountId.Should().Be(7);
        // The create seeded the cache, so resolving needs no follow-up GET: only the POST was sent.
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task Resolve_FetchesOnce_ThenServesFromCache()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(IsGetSession, _ => Json(CreateResponse(42)));

        var first = await client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);
        var second = await client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);

        first.Should().Be(42);
        second.Should().Be(42);
        handler
            .Requests.Count(r =>
                r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.EndsWith($"/sandboxes/{SessionId}", StringComparison.Ordinal)
            )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Resolve_MissingWorkspaceId_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(IsGetSession, _ => Json(CreateResponse(mountId: null)));

        var act = () => client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task Resolve_MissingSession_IsNotFound()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(IsGetSession, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [Fact]
    public async Task Delete_EvictsMountIdCache_SoNextResolveRefetches()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(IsGetSession, _ => Json(CreateResponse(7)));
        handler.OnStatus(HttpMethod.Delete, $"/api/v1/sandboxes/{SessionId}", HttpStatusCode.NoContent);

        // Warm the cache, delete the sandbox, then resolve again. A stale cached mount id would let the
        // second resolve serve a mapping for a session that no longer exists; DeleteAsync must instead
        // evict it so this resolve issues a FRESH GET.
        _ = await client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);
        await client.DeleteAsync(SessionId);
        _ = await client.ResolveWorkspaceMountIdAsync(SessionId, CancellationToken.None);

        handler
            .Requests.Count(r =>
                r.Method == HttpMethod.Get
                && r.Uri.AbsolutePath.EndsWith($"/sandboxes/{SessionId}", StringComparison.Ordinal)
            )
            .Should()
            .Be(2);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
