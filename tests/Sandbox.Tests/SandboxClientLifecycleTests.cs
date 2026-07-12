using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientLifecycleTests
{
    private const string CreateResponseJson = """
        {"session_id":"sess-1","container_id":"container-1","volumes":{"workspace":{"container_path":"/workspace","read_only":false}}}
        """;

    [Fact]
    public async Task CreateAsync_HappyPath_ReturnsSandboxInfo()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var info = await client.CreateAsync(new SandboxCreateRequest("my-workspace"));

        info.SessionId.Should().Be("sess-1");
        info.ContainerId.Should().Be("container-1");
        info.WorkspaceContainerPath.Should().Be("/workspace");
    }

    [Fact]
    public async Task CreateAsync_ExactRestWireShape_MatchesGatewayContract()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var request = new SandboxCreateRequest(
            "my-workspace",
            marketplaces: ["official"],
            authProviders: [new SandboxAuthProvider("github-auth", "webhook", "https://app/cb", "shared-secret", 300, ["repo"])],
            networkRules: [new SandboxNetworkRule("github", "allow", hosts: ["github.com"], ports: [443], priority: 100)],
            discovery: new SandboxDiscoverySettings("https://app/discovery", "discovery-secret")
        );

        _ = await client.CreateAsync(request);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;

        body.GetProperty("app").GetProperty("id").GetString().Should().Be("app-1");
        body.GetProperty("workspace").GetString().Should().Be("my-workspace");
        body.GetProperty("marketplaces")[0].GetString().Should().Be("official");

        var authProvider = body.GetProperty("auth_providers")[0];
        authProvider.GetProperty("id").GetString().Should().Be("github-auth");
        authProvider.GetProperty("gateway_auth").GetString().Should().Be("shared-secret");
        authProvider.GetProperty("cache_ttl_seconds").GetInt32().Should().Be(300);

        var networkRule = body.GetProperty("network").GetProperty("rules")[0];
        networkRule.GetProperty("id").GetString().Should().Be("github");
        networkRule.GetProperty("hosts")[0].GetString().Should().Be("github.com");
        networkRule.GetProperty("ports")[0].GetInt32().Should().Be(443);

        var discovery = body.GetProperty("discovery").GetProperty("webhook");
        discovery.GetProperty("url").GetString().Should().Be("https://app/discovery");
        discovery.GetProperty("auth_header").GetString().Should().Be("discovery-secret");
    }

    [Fact]
    public async Task CreateAsync_EmptyOptionalCollections_OmitsFieldsFromWireBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;

        body.TryGetProperty("auth_providers", out _).Should().BeFalse();
        body.TryGetProperty("network", out _).Should().BeFalse();
        body.TryGetProperty("discovery", out _).Should().BeFalse();
        body.TryGetProperty("marketplaces", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_ResponseWithUnknownFields_IsTolerated()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"sess-1","container_id":"container-1","unexpected_field":{"nested":true},"volumes":{"workspace":{"container_path":"/workspace"}}}"""
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.SessionId.Should().Be("sess-1");
    }

    [Fact]
    public async Task GetAsync_HappyPath_ReturnsSandboxInfo()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1", CreateResponseJson);

        var info = await client.GetAsync("sess-1");

        info.SessionId.Should().Be("sess-1");
    }

    [Fact]
    public async Task ListAsync_HappyPath_ReturnsAllSandboxes()
    {
        // Real gateway list shape (verified against SandboxedOsToolsMcpServer@c0dc9cfe
        // crates/mcp-gateway/src/api/sandboxes.rs::list_sandboxes): each entry is a flattened Docker
        // container (`id`, `state`, `status`, `running`, ...) plus `session_id` — NOT the
        // create/get response shape. The container id field is `id`, never `container_id`.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes",
            """
            {"sandboxes":[
                {"id":"c1","state":"running","status":"Up 2 minutes","running":true,"session_id":"sess-1"},
                {"id":"c2","state":"running","status":"Up 5 minutes","running":true,"session_id":"sess-2"}
            ]}
            """
        );

        var infos = await client.ListAsync();

        infos.Should().HaveCount(2);
        infos.Select(i => i.SessionId).Should().BeEquivalentTo(["sess-1", "sess-2"]);
        infos.Select(i => i.ContainerId).Should().BeEquivalentTo(["c1", "c2"]);
        infos.Should().OnlyContain(i => i.WorkspaceContainerPath == null);
    }

    [Fact]
    public async Task ListAsync_EntryWithNullSessionId_IsOmitted()
    {
        // A live container the gateway hasn't attributed to any session (or a dormant record with a
        // gone container) reports session_id: null. SandboxInfo requires a non-null session id, so
        // such an entry must be skipped rather than crash or fabricate one.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes",
            """{"sandboxes":[{"id":"unowned","state":"running","status":"Up","running":true,"session_id":null},{"id":"c2","session_id":"sess-2"}]}"""
        );

        var infos = await client.ListAsync();

        infos.Should().ContainSingle();
        infos.Single().SessionId.Should().Be("sess-2");
    }

    [Fact]
    public async Task ListAsync_EmptyBody_ReturnsEmptyList()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes", "{}");

        var infos = await client.ListAsync();

        infos.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_NullEntryElement_ThrowsProtocol_NotNullReference()
    {
        // A null array element is a malformed collection element (distinct from an entry whose
        // session_id is null, which is a valid-but-omitted case). Reading entry.SessionId off a null
        // element would otherwise throw a raw NullReferenceException; it must map to Protocol.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes", """{"sandboxes":[{"id":"c1","session_id":"sess-1"},null]}""");

        var exception = await Record.ExceptionAsync(() => client.ListAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task DeleteAsync_HappyPath_SucceedsWithoutThrowing()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1", HttpStatusCode.NoContent);

        await client.DeleteAsync("sess-1");

        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("GetAsync")]
    [InlineData("DeleteAsync")]
    public async Task GetOrDelete_UniformNotFound_MapsToNotFoundRegardlessOfBody(string operation)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/api/v1/sandboxes/missing", HttpStatusCode.NotFound);
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/missing", HttpStatusCode.NotFound);

        Func<Task> act = operation == "GetAsync" ? () => client.GetAsync("missing") : () => client.DeleteAsync("missing");

        var exception = await act.Should().ThrowAsync<SandboxException>();
        exception.Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        exception.Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task GetAsync_Foreign404AndMissing404_BothMapToNotFoundUniformly()
    {
        // The gateway returns 404 both for a session that never existed and for one owned by a
        // different app id — the SDK must classify both identically without trying to distinguish
        // them from response content.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/missing", """{"error":"session not found"}""", HttpStatusCode.NotFound);
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/foreign", """{"error":"session not found"}""", HttpStatusCode.NotFound);

        var missing = await Record.ExceptionAsync(() => client.GetAsync("missing"));
        var foreign = await Record.ExceptionAsync(() => client.GetAsync("foreign"));

        missing.Should().BeOfType<SandboxException>().Which.Kind.Should().Be(SandboxErrorKind.NotFound);
        foreign.Should().BeOfType<SandboxException>().Which.Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [Fact]
    public async Task GetAsync_Empty401_MapsToAuthorizationWithoutThrowingOnEmptyBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/api/v1/sandboxes/sess-1", HttpStatusCode.Unauthorized);

        var exception = await Record.ExceptionAsync(() => client.GetAsync("sess-1"));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Authorization);
    }

    [Fact]
    public async Task CreateAsync_UnexpectedStatus_MapsToProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Post, "/api/v1/sandboxes", HttpStatusCode.InternalServerError);

        var exception = await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws")));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task AuthHeaders_AreStampedOnEveryRestCall()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1", CreateResponseJson);
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1", HttpStatusCode.OK);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));
        _ = await client.GetAsync("sess-1");
        await client.DeleteAsync("sess-1");

        handler.Requests.Should().OnlyContain(r => r.SbxAppId == "app-1");
        handler.Requests.Should().OnlyContain(r => r.SbxAppKey == TestSupport.ValidSecret);
    }
}
