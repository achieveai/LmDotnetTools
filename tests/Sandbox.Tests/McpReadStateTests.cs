using System.Net;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public sealed class McpReadStateTests
{
    private const string Resolved = """
        {"egress":"auto","servers":[{"name":"sdk-contract-public","plugin":"skilltiers","marketplace":"lmtest",
        "transport":"http","url":"https://mcp.example.invalid/mcp","auth":"none","headers":[],
        "rule_id":"mcp:sdk-contract-public","unresolved_vars":[]}]}
        """;
    private const string Manifest = """
        {"schema":"sbx.mcp-servers/1","session_id":"s","servers":[{"name":"sdk-contract-public","plugin":"skilltiers",
        "marketplace":"lmtest","transport":"http","url":"https://mcp.example.invalid/mcp",
        "url_template":"https://mcp.example.invalid/mcp","headers":{},"auth":{"mode":"none","provider":null,"headers":[]},
        "egress":"auto","unresolved_vars":[],"generated":null}]}
        """;

    [Fact]
    public async Task CreateGetAndManifest_PreserveDistinctTypedSnapshotsAndAuthenticatedRead()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", "{\"session_id\":\"s\",\"mcp\":" + Resolved + "}");
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s", "{\"session_id\":\"s\",\"mcp\":" + Resolved + "}");
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s/mcp-servers/manifest", Manifest);
        var created = await client.CreateAsync(new SandboxCreateRequest("ws") { Mcp = new(SandboxMcpEgress.Auto) });
        var current = await client.GetAsync("s");
        var manifest = await client.GetMcpManifestAsync("s");
        created.Mcp.Should().NotBeNull();
        created.Mcp!.Egress.Should().Be(SandboxMcpEgress.Auto);
        current.Mcp.Should().NotBeSameAs(created.Mcp);
        var binding = current.Mcp!.Servers.Should().ContainSingle().Subject;
        (binding.Name, binding.Plugin, binding.Marketplace, binding.Transport, binding.Auth)
            .Should()
            .Be(("sdk-contract-public", "skilltiers", "lmtest", "http", SandboxMcpAuthMode.None));
        binding.ProviderId.Should().BeNull();
        binding.RuleId.Should().Be("mcp:sdk-contract-public");
        var server = manifest.Servers.Should().ContainSingle().Subject;
        manifest.Schema.Should().Be("sbx.mcp-servers/1");
        manifest.SessionId.Should().Be("s");
        server.Supported.Should().BeTrue("HTTP omits supported on the producer wire");
        server.Auth.Mode.Should().Be(SandboxMcpAuthMode.None);
        server.Auth.Provider.Should().BeNull();
        server.Egress.Should().Be(SandboxMcpManifestEgress.Auto);
        server.UrlTemplate.Should().Be(binding.Url);
        server.Generated.Should().BeNull();
        Action mutate = () => ((IList<SandboxMcpResolvedServer>)created.Mcp.Servers).Clear();
        mutate.Should().Throw<NotSupportedException>();
        Action mutateHeaders = () => ((IDictionary<string, string>)server.Headers).Add("x", "y");
        mutateHeaders.Should().Throw<NotSupportedException>();
        handler
            .Requests.Should()
            .HaveCount(3)
            .And.OnlyContain(r => r.SbxAppId == "app-1" && r.SbxAppKey == TestSupport.ValidSecret);
        handler.Requests.Last().SessionId.Should().BeNull();
        manifest.ToString().Should().NotContain("mcp.example.invalid");
        server.ToString().Should().NotContain("mcp.example.invalid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalAcknowledgment_DistinguishesOmittedFromPresentEmptyWithoutCleanup(bool present)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        var body = present
            ? "{\"session_id\":\"s\",\"mcp\":{\"egress\":\"manual\",\"servers\":[]}}"
            : "{\"session_id\":\"s\"}";
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", body);
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s", body);
        var request = new SandboxCreateRequest("ws") { Mcp = new() };
        var created = await client.CreateAsync(request);
        var missingAcknowledgment = request.Mcp is not null && created.Mcp is null;
        missingAcknowledgment.Should().Be(!present);
        created.SessionId.Should().Be("s");
        (await client.GetAsync("s")).Mcp.Should().BeEquivalentTo(created.Mcp);
        if (present)
        {
            created.Mcp!.Servers.Should().BeEmpty();
        }
        handler.Requests.Should().HaveCount(2).And.NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void ExistingSixteenArgumentConstructor_RemainsAvailableWithUnknownMcp()
    {
        Type[] signature =
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(long?),
            typeof(string),
            typeof(SandboxInventory),
            typeof(SandboxPluginResolution),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(SandboxImageReport),
            typeof(string),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(IReadOnlyList<string>),
            typeof(SandboxWebhookInfo),
            typeof(SandboxSkillsPolicy),
        ];
        typeof(SandboxInfo).GetConstructor(signature).Should().NotBeNull();
        new SandboxInfo("s").Mcp.Should().BeNull();
    }

    [Theory]
    [InlineData(404, "mcp_manifest_unavailable", SandboxErrorKind.NotFound)]
    [InlineData(503, "marketplaces_unavailable", SandboxErrorKind.Unavailable)]
    [InlineData(404, null, SandboxErrorKind.NotFound)]
    public async Task Manifest_UnavailableRemainsDistinctFromValidEmpty(int status, string? code, SandboxErrorKind kind)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s/mcp-servers/manifest",
            System.Text.Json.JsonSerializer.Serialize(new { error_code = code }),
            (HttpStatusCode)status
        );
        var error = (await Record.ExceptionAsync(() => client.GetMcpManifestAsync("s")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
        error.Kind.Should().Be(kind);
        error.ErrorCode.Should().Be(code);
        var (emptyClient, emptyHandler) = TestSupport.CreateBorrowedClient();
        emptyHandler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s/mcp-servers/manifest",
            """{"schema":"sbx.mcp-servers/1","session_id":"s","servers":[]}"""
        );
        (await emptyClient.GetMcpManifestAsync("s")).Servers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schema\":\"unknown\",\"session_id\":\"s\",\"servers\":[]}")]
    [InlineData("{\"schema\":\"sbx.mcp-servers/1\",\"session_id\":\"foreign\",\"servers\":[]}")]
    [InlineData("{\"schema\":\"sbx.mcp-servers/1\",\"session_id\":\"s\"}")]
    public async Task Manifest_MalformedOrWrongIdentity_UsesFixedProtocolDiagnostics(string body)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s/mcp-servers/manifest", body);
        var error = (await Record.ExceptionAsync(() => client.GetMcpManifestAsync("s")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
        error.Kind.Should().Be(SandboxErrorKind.Protocol);
        error.InnerException.Should().BeNull();
        error.Message.Should().Be("Sandbox gateway returned malformed MCP response data.");
    }

    [Fact]
    public async Task CurrentReads_PreserveProducerTemporalStateWithoutMutatingCreateSnapshot()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", "{\"session_id\":\"s\",\"mcp\":" + Resolved + "}");
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s",
            "{\"session_id\":\"s\",\"mcp\":" + Resolved.Replace("mcp.example.invalid", "after.example.invalid") + "}"
        );
        var node = System.Text.Json.Nodes.JsonNode.Parse(Manifest)!;
        var row = node["servers"]![0]!;
        row["url"] = "https://after.example.invalid/mcp";
        row["url_template"] = "https://${HOST}/mcp";
        row["headers"]!["X-Fixture-Marker"] = "after";
        row["egress"] = "stale";
        row["note"] = "host changed";
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s/mcp-servers/manifest", node.ToJsonString());
        var created = await client.CreateAsync(new SandboxCreateRequest("ws"));
        var current = await client.GetAsync("s");
        var manifest = await client.GetMcpManifestAsync("s");
        created.Mcp!.Servers[0].Url.Should().Be("https://mcp.example.invalid/mcp");
        current.Mcp!.Servers[0].Url.Should().Be("https://after.example.invalid/mcp");
        current.Mcp.Servers[0].RuleId.Should().Be(created.Mcp.Servers[0].RuleId);
        manifest.Servers[0].Egress.Should().Be(SandboxMcpManifestEgress.Stale);
        manifest.Servers[0].Note.Should().Be("host changed");
        manifest.Servers[0].UrlTemplate.Should().Be("https://${HOST}/mcp");
        manifest.Servers[0].Headers["X-Fixture-Marker"].Should().Be("after");
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("auth")]
    [InlineData("null-header-value")]
    [InlineData("null-row")]
    [InlineData("unknown-auth")]
    [InlineData("unknown-egress")]
    [InlineData("missing-names")]
    [InlineData("null-name-element")]
    [InlineData("missing-identity")]
    [InlineData("non-http-without-supported")]
    public async Task Manifest_InvalidNestedContracts_AreProtocol(string invalid)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Manifest)!;
        var row = node["servers"]![0]!;
        switch (invalid)
        {
            case "headers":
                row.AsObject().Remove("headers");
                break;
            case "auth":
                row["auth"] = null;
                break;
            case "null-header-value":
                row["headers"]!["x"] = null;
                break;
            case "null-row":
                node["servers"]![0] = null;
                break;
            case "unknown-auth":
                row["auth"]!["mode"] = "unknown";
                break;
            case "unknown-egress":
                row["egress"] = "unknown";
                break;
            case "missing-names":
                row.AsObject().Remove("unresolved_vars");
                break;
            case "null-name-element":
                row["auth"]!["headers"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
                break;
            case "missing-identity":
                row.AsObject().Remove("name");
                break;
            case "non-http-without-supported":
                row["transport"] = "sse";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalid));
        }
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s/mcp-servers/manifest", node.ToJsonString());
        var error = (await Record.ExceptionAsync(() => client.GetMcpManifestAsync("s")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
        error.Kind.Should().Be(SandboxErrorKind.Protocol);
        error.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task Manifest_UnsupportedTransportAndGeneratedMetadata_RemainProducerData()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Manifest)!;
        var row = node["servers"]![0]!;
        row["transport"] = "sse";
        row["supported"] = false;
        row["unsupported_reason"] = "library transport unsupported";
        row["egress"] = "manual";
        row["auth"]!["mode"] = "injected";
        row["auth"]!["provider"] = "mcp:sdk-contract-public";
        row["auth"]!["headers"] = new System.Text.Json.Nodes.JsonArray("Authorization");
        row["generated"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"plugin":"generated","marketplace":"generated-market","skill":"call","tools_hash":"hash","lib_path":"lib/client.py"}"""
        );
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s/mcp-servers/manifest", node.ToJsonString());
        var server = (await client.GetMcpManifestAsync("s")).Servers.Single();
        (server.Name, server.Plugin, server.Marketplace, server.Transport, server.Supported)
            .Should()
            .Be(("sdk-contract-public", "skilltiers", "lmtest", "sse", false));
        server.UnsupportedReason.Should().Be("library transport unsupported");
        server.Egress.Should().Be(SandboxMcpManifestEgress.Manual);
        server.Auth.Mode.Should().Be(SandboxMcpAuthMode.Injected);
        server.Auth.Provider.Should().Be("mcp:sdk-contract-public");
        server.Auth.Headers.Should().Equal("Authorization");
        var generated = server.Generated!;
        (generated.Plugin, generated.Marketplace, generated.Skill, generated.ToolsHash, generated.LibPath)
            .Should()
            .Be(("generated", "generated-market", "call", "hash", "lib/client.py"));
        generated.ToString().Should().NotContain("lib/client.py");
    }

    [Theory]
    [InlineData("{\"egress\":\"unknown\",\"servers\":[]}")]
    [InlineData("{\"egress\":\"auto\"}")]
    [InlineData("{\"egress\":\"auto\",\"servers\":[null]}")]
    [InlineData("{\"egress\":42,\"servers\":[]}")]
    public async Task Lifecycle_MalformedResolvedState_IsProtocolWithoutRawJsonException(string state)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s", "{\"session_id\":\"s\",\"mcp\":" + state + "}");
        var error = (await Record.ExceptionAsync(() => client.GetAsync("s")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;
        error.Kind.Should().Be(SandboxErrorKind.Protocol);
        error.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task Manifest_UnsupportedCharset_IsProtocolWithoutPayloadDiagnostics()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            _ => true,
            _ =>
            {
                var content = new StringContent("""{"schema":"sbx.mcp-servers/1","session_id":"s","servers":[]}""");
                content.Headers.ContentType = new("application/json") { CharSet = "unsupported-test-charset" };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        );
        var thrown = await Record.ExceptionAsync(() => client.GetMcpManifestAsync("s"));
        var error = thrown.Should().BeOfType<SandboxException>().Subject;
        error.Kind.Should().Be(SandboxErrorKind.Protocol);
        error.StatusCode.Should().Be(200);
        error.InnerException.Should().BeNull();
        error.ToString().Should().NotContain("unsupported-test-charset");
    }
}
