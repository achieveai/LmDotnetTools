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
        mcp.GetProperty("egress").GetString().Should().Be(expected);
        var server = mcp.GetProperty("servers").GetProperty("stub");
        server.GetProperty("auth").GetString().Should().Be("injected");
        server.GetProperty("headers")[0].GetString().Should().Be("Authorization");
        server.GetProperty("cache_ttl_seconds").GetInt32().Should().Be(7);
        server.GetProperty("enabled").GetBoolean().Should().BeFalse();
        server.GetProperty("url").GetString().Should().Be("https://mcp.example/mcp");
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
