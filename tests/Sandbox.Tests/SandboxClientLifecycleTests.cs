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
        networkRule.TryGetProperty("auth_provider", out _).Should().BeFalse();

        var discovery = body.GetProperty("discovery").GetProperty("webhook");
        discovery.GetProperty("url").GetString().Should().Be("https://app/discovery");
        discovery.GetProperty("auth_header").GetString().Should().Be("discovery-secret");
    }

    [Fact]
    public async Task CreateAsync_NetworkRuleWithoutAuthProvider_OmitsAuthProviderFromWireBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var request = new SandboxCreateRequest(
            "my-workspace",
            networkRules: [new SandboxNetworkRule("open", "allow", hosts: ["example.com"])]
        );

        _ = await client.CreateAsync(request);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        var networkRule = body.GetProperty("network").GetProperty("rules")[0];

        // A present-but-empty "auth_provider" is `Some("")` on the gateway's NetworkRule — a
        // provider-id lookup it fails — not "no provider". The field must be absent entirely.
        networkRule.TryGetProperty("auth_provider", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_NetworkRuleWithAuthProvider_IncludesAuthProviderInWireBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var request = new SandboxCreateRequest(
            "my-workspace",
            networkRules: [new SandboxNetworkRule("github", "allow", hosts: ["github.com"], authProvider: "github-auth")]
        );

        _ = await client.CreateAsync(request);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        var networkRule = body.GetProperty("network").GetProperty("rules")[0];

        networkRule.GetProperty("auth_provider").GetString().Should().Be("github-auth");
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

    [Fact]
    public async Task ListAsync_OversizeDeclaredControlPlaneBody_ThrowsProtocol_BeforeBuffering()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // A control-plane 2xx that declares a body far larger than the read cap must be refused by its
        // declared Content-Length before it is buffered whole — the same bound as the direct downloads.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new OversizedContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        var exception = await Record.ExceptionAsync(() => client.ListAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ListAsync_ChunkedOverCapControlPlaneBody_ThrowsProtocol_WhileStreaming()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // A control-plane 2xx with NO Content-Length (chunked) whose body streams past the cap: the header
        // precheck can't catch it, so the streamed running-byte-count cap must reject it mid-stream. The
        // lazy zero-stream produces the bytes without allocating them up front.
        handler.On(
            req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnsizedStreamContent(SandboxClient.MaxDirectReadBytes + 1) }
        );

        var exception = await Record.ExceptionAsync(() => client.ListAsync());

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task CreateAsync_ExplicitPluginSelection_IncludedInWireBodyAsPluginSelectionField()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var request = new SandboxCreateRequest(
            "my-workspace",
            pluginSelection: [new SandboxPluginRef("official", "code-review")]
        );

        _ = await client.CreateAsync(request);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        var plugin = body.GetProperty("pluginSelection")[0];

        plugin.GetProperty("marketplace").GetString().Should().Be("official");
        plugin.GetProperty("plugin").GetString().Should().Be("code-review");
    }

    [Fact]
    public async Task CreateAsync_ExplicitEmptyPluginSelection_SendsEmptyPluginSelectionArray_NotOmitted()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var request = new SandboxCreateRequest("my-workspace", pluginSelection: []);

        _ = await client.CreateAsync(request);

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;

        body.GetProperty("pluginSelection").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_NullPluginSelection_OmitsPluginSelectionFieldFromWireBody()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;

        body.TryGetProperty("pluginSelection", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_ResponseWithPluginResolution_ParsesIntoSandboxInfo()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","container_id":"container-1",
             "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
             "pluginResolution":{"supported":true,
               "requested":[{"marketplace":"official","plugin":"code-review"}],
               "effective":[{"marketplace":"official","plugin":"code-review"}],
               "failed":[]}}
            """
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.PluginResolution.Should().NotBeNull();
        info.PluginResolution!.Supported.Should().BeTrue();
        info.PluginResolution.Effective.Should().ContainSingle(r => r.Plugin == "code-review");
    }

    /// <summary>
    /// The partial-block case the "all four arrays present" test above never reaches: the gateway
    /// reports a resolution but omits <c>requested</c> entirely — exactly what it sends when the
    /// caller made no explicit selection. <c>Requested</c> is tri-state, so it must survive as
    /// <see langword="null"/> rather than collapsing to an empty list (which would read as
    /// "explicitly no plugins"), and the partial block must not throw.
    /// </summary>
    [Fact]
    public async Task CreateAsync_PluginResolutionWithoutRequested_LeavesRequestedNull_AndDoesNotThrow()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","container_id":"container-1",
             "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
             "pluginResolution":{"supported":true}}
            """
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        // Supported binding proves the block itself parsed, so a null Requested is the mapped
        // tri-state and not the whole resolution having fallen back to null.
        info.PluginResolution.Should().NotBeNull();
        info.PluginResolution!.Supported.Should().BeTrue();
        info.PluginResolution.Requested.Should().BeNull();
    }

    /// <summary>
    /// As above, but <c>requested</c> is present as an explicit JSON <c>null</c>. Omission and
    /// explicit null reach the deserializer differently, so both are pinned; both must land on a
    /// <see langword="null"/> <c>Requested</c>.
    /// </summary>
    [Fact]
    public async Task CreateAsync_PluginResolutionWithExplicitNullRequested_LeavesRequestedNull_AndDoesNotThrow()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","container_id":"container-1",
             "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
             "pluginResolution":{"supported":true,"requested":null,"effective":[],"failed":[]}}
            """
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.PluginResolution.Should().NotBeNull();
        info.PluginResolution!.Supported.Should().BeTrue();
        info.PluginResolution.Requested.Should().BeNull();

        // Effective/Failed are NOT tri-state — they normalize null/absent to empty. Pinned here so a
        // future "make everything tri-state" change cannot silently flip them to null.
        info.PluginResolution.Effective.Should().BeEmpty();
        info.PluginResolution.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_ResponseWithoutPluginResolution_LeavesItNull()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.PluginResolution.Should().BeNull();
    }

    #region Malformed plugin-resolution entries on a 2xx response

    /// <summary>
    /// The wire DTOs model a plugin ref's <c>marketplace</c>/<c>plugin</c> as non-nullable
    /// <see langword="string"/>, but nothing enforces that at deserialization: the SDK's
    /// <see cref="Wire.SandboxJson.RestOptions"/> does not set <c>RespectNullableAnnotations</c>, so
    /// System.Text.Json writes a JSON <c>null</c> straight into a non-nullable member, and a JSON
    /// <c>null</c> ARRAY ELEMENT deserializes to a null reference in the list. A semantically-invalid
    /// 2xx body therefore reaches the mapper intact, and each of these cases would otherwise leave
    /// this SDK as a raw <see cref="NullReferenceException"/> or <see cref="ArgumentException"/> —
    /// the same class of leak already closed for the marketplace-preview and discovered-items paths.
    /// </summary>
    private static async Task<Exception> CreateWithResolutionAsync(string resolutionJson)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","container_id":"container-1",
             "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
             "pluginResolution":
            """
                + resolutionJson
                + "}"
        );

        return await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws")));
    }

    [Fact]
    public async Task CreateAsync_PluginResolutionWithNullArrayElement_ThrowsProtocolNamingTheArray()
    {
        var thrown = await CreateWithResolutionAsync("""{"supported":true,"effective":[null]}""");

        thrown.Should().BeOfType<SandboxException>("a malformed 2xx payload is a protocol defect, not an unhandled NullReferenceException");
        var sandboxException = (SandboxException)thrown;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().Contain("effective", "a bare 'malformed response' leaves the next reader to bisect three arrays");
    }

    [Fact]
    public async Task CreateAsync_PluginResolutionEntryWithNullField_ThrowsProtocolNamingTheArray()
    {
        var thrown = await CreateWithResolutionAsync("""{"supported":true,"requested":[{"marketplace":null,"plugin":"code-review"}]}""");

        thrown.Should().BeOfType<SandboxException>("SandboxPluginRef's own guard throws ArgumentNullException, which is not this SDK's error contract");
        var sandboxException = (SandboxException)thrown;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().Contain("requested");
    }

    [Fact]
    public async Task CreateAsync_PluginResolutionEntryWithBlankField_ThrowsProtocolNamingTheArray()
    {
        var thrown = await CreateWithResolutionAsync("""{"supported":true,"failed":[{"marketplace":"official","plugin":"   "}]}""");

        thrown.Should().BeOfType<SandboxException>("a whitespace-only plugin id is as unusable as a missing one");
        var sandboxException = (SandboxException)thrown;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().Contain("failed");
    }

    /// <summary>
    /// The positive control. Without it, a guard that rejected every entry — or one that dropped
    /// them all — would satisfy every negative test above while destroying the feature.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WellFormedPluginResolution_StillParsesEveryArray()
    {
        var thrown = await CreateWithResolutionAsync(
            """
            {"supported":true,
             "requested":[{"marketplace":"official","plugin":"code-review"},{"marketplace":"official","plugin":"docs"}],
             "effective":[{"marketplace":"official","plugin":"code-review"}],
             "failed":[{"marketplace":"official","plugin":"docs"}]}
            """
        );

        thrown.Should().BeNull("a well-formed payload must not be rejected by the malformed-entry guards");
    }

    /// <summary>
    /// Same well-formed payload, read back through the public model. Kept separate from the
    /// no-throw control above so a regression tells you WHICH of the two broke.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WellFormedPluginResolution_SurfacesEveryEntryVerbatim()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","container_id":"container-1",
             "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
             "pluginResolution":{"supported":true,
               "requested":[{"marketplace":"official","plugin":"code-review"},{"marketplace":"official","plugin":"docs"}],
               "effective":[{"marketplace":"official","plugin":"code-review"}],
               "failed":[{"marketplace":"official","plugin":"docs"}]}}
            """
        );

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.PluginResolution!.Requested.Should().HaveCount(2);
        info.PluginResolution.Requested![1].Plugin.Should().Be("docs");
        info.PluginResolution.Effective.Should().ContainSingle(r => r.Marketplace == "official" && r.Plugin == "code-review");
        info.PluginResolution.Failed.Should().ContainSingle(r => r.Plugin == "docs");
    }

    #endregion

    /// <summary>An <see cref="HttpContent"/> that declares a large <c>Content-Length</c> without allocating any bytes, to exercise the pre-read size guard.</summary>
    private sealed class OversizedContent : HttpContent
    {
        private readonly long _length;

        public OversizedContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }

    /// <summary>
    /// An <see cref="HttpContent"/> that reports NO <c>Content-Length</c> (chunked) and whose read stream
    /// lazily yields a fixed number of zero bytes without allocating them — exercising the STREAMING byte
    /// cap (not the header precheck) with negligible up-front memory.
    /// </summary>
    private sealed class UnsizedStreamContent : HttpContent
    {
        private readonly long _length;

        public UnsizedStreamContent(long length) => _length = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            new ZeroStream(_length).CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ZeroStream(_length));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A read-only forward stream that yields <paramref name="length"/> zero bytes then EOF, without allocating them.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var produced = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, produced);
            _remaining -= produced;
            return produced;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
