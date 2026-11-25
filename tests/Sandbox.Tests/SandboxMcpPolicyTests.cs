using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public sealed class SandboxMcpPolicyTests
{
    private const string Created =
        """{"session_id":"sess-1","container_id":"container-1","volumes":{"workspace":{"container_path":"/workspace","read_only":false}}}""";

    [Fact]
    public async Task CreateAsync_NoMcp_PreservesThePublishedConstructorAndOmittedWireField()
    {
        Type[] signature =
        [
            typeof(string),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<SandboxAuthProvider>),
            typeof(IReadOnlyList<SandboxNetworkRule>),
            typeof(SandboxDiscoverySettings),
            typeof(IReadOnlyList<SandboxPluginRef>),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(IReadOnlyList<SandboxPluginMount>),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyDictionary<string, string>),
            typeof(SandboxWebhookSettings),
            typeof(SandboxSkillsPolicy),
        ];
        typeof(SandboxCreateRequest).GetConstructor(signature).Should().NotBeNull();
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", Created);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.TryGetProperty("mcp", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(SandboxMcpEgress.Auto, "auto")]
    [InlineData(SandboxMcpEgress.Manual, "manual")]
    public async Task CreateAsync_McpPolicy_SendsExactSecretFreeOverridesFromACopiedSnapshot(
        SandboxMcpEgress mode,
        string expected
    )
    {
        var headers = new[] { "Authorization" };
        var servers = new Dictionary<string, SandboxMcpServerPolicy>
        {
            ["stub"] = new(SandboxMcpAuthMode.Injected, headers, 7, false, "https://mcp.example/mcp"),
        };
        var policy = new SandboxMcpPolicy(mode, servers);
        headers[0] = "Changed";
        servers.Clear();
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", Created);

        _ = await client.CreateAsync(new SandboxCreateRequest("ws") { Mcp = policy });

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var mcp = sent.RootElement.GetProperty("mcp");
        mcp.TryGetProperty("direct", out _).Should().BeFalse();
        mcp.GetProperty("egress").GetString().Should().Be(expected);
        var server = mcp.GetProperty("servers").GetProperty("stub");
        server.GetProperty("auth").GetString().Should().Be("injected");
        server.GetProperty("headers")[0].GetString().Should().Be("Authorization");
        server.GetProperty("cache_ttl_seconds").GetInt32().Should().Be(7);
        server.GetProperty("enabled").GetBoolean().Should().BeFalse();
        server.GetProperty("url").GetString().Should().Be("https://mcp.example/mcp");
    }

    [Fact]
    public void DirectMcp_EnforcesTheGatewayEndpointLimit()
    {
        var direct = Enumerable
            .Range(0, 65)
            .Select(i => new SandboxDirectMcpServer($"g-w-{i:x24}", $"server-{i}", $"https://mcp{i}.example/"))
            .ToArray();

        new SandboxMcpPolicy(SandboxMcpEgress.Manual, servers: null, direct[..64]).Direct.Should().HaveCount(64);
        var createPolicy = () => new SandboxMcpPolicy(SandboxMcpEgress.Manual, servers: null, direct);
        createPolicy.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("direct");
    }

    [Fact]
    public async Task CreateAsync_DirectMcp_RequestsAndAcknowledgesTheExactEndpointWithoutSecrets()
    {
        const string alias = "g-w-0123456789abcdef01234567";
        const string url = "https://mcp.example/";
        var direct = new List<SandboxDirectMcpServer> { new(alias, "fred", url) };
        var policy = new SandboxMcpPolicy(SandboxMcpEgress.Manual, servers: null, direct);
        direct.Clear();
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            Created[..^1]
                + $$$""","mcp":{"egress":"manual","servers":[{"name":"fred","plugin":"direct","marketplace":"{{{alias}}}","transport":"http","url":"{{{url}}}","auth":"none","headers":[],"unresolved_vars":[],"direct":true}]}}"""
        );

        var created = await client.CreateAsync(new SandboxCreateRequest("ws") { Mcp = policy });

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var declaration = sent.RootElement.GetProperty("mcp").GetProperty("direct").EnumerateArray().Single();
        declaration
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("marketplace", "name", "url");
        declaration.GetProperty("marketplace").GetString().Should().Be(alias);
        declaration.GetProperty("name").GetString().Should().Be("fred");
        declaration.GetProperty("url").GetString().Should().Be(url);
        var acknowledgement = created.Mcp!.Servers.Single();
        acknowledgement.Direct.Should().BeTrue();
        acknowledgement.Marketplace.Should().Be(alias);
        acknowledgement.Url.Should().Be(url);
    }

    [Fact]
    public async Task CreateAsync_PublicHttpServer_PreservesNoneAuthAndOmitsUnsetOverrides()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", Created);

        _ = await client.CreateAsync(
            new SandboxCreateRequest("ws")
            {
                Mcp = new SandboxMcpPolicy(
                    servers: new Dictionary<string, SandboxMcpServerPolicy>
                    {
                        ["public"] = new(auth: SandboxMcpAuthMode.None, url: "http://public.example/mcp"),
                    }
                ),
            }
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var mcp = sent.RootElement.GetProperty("mcp");
        mcp.TryGetProperty("egress", out _).Should().BeFalse();
        var server = mcp.GetProperty("servers").GetProperty("public");
        server.GetProperty("auth").GetString().Should().Be("none");
        server.GetProperty("url").GetString().Should().Be("http://public.example/mcp");
        server.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("auth", "url");
    }
}
