using System.Net;
using System.Text;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     Verifies the transparent MCP (Streamable HTTP) reverse proxy on <c>/mcp</c> and
///     <c>/mcp/readonly</c>: raw body/method/path forwarding, that every inbound header passes
///     through verbatim except <c>Authorization</c> (which the proxy overrides with its own
///     Copilot credentials), and session-id passthrough.
/// </summary>
public sealed class McpProxyTests
{
    private static StringContent JsonRpc(string body) => new(body, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/readonly")]
    public async Task Post_forwards_the_raw_json_rpc_body_verbatim_to_the_same_upstream_path(string path)
    {
        HttpRequestMessage? forwarded = null;
        string? forwardedBody = null;
        await using var factory = new ProxyWebAppFactory(
            async (req, ct) =>
            {
                forwarded = req;
                forwardedBody = await req.Content!.ReadAsStringAsync(ct);
                return TestUpstream.Json(
                    "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{\"protocolVersion\":\"2025-06-18\"}}",
                    headers: new Dictionary<string, string>
                    {
                        ["mcp-session-id"] = "19d9ea76-c0b5-44bd-b554-f6e5e6127461",
                    }
                );
            }
        );
        using var client = factory.CreateClient();

        const string requestBody = "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"initialize\",\"params\":{}}";
        using var response = await client.PostAsync(path, JsonRpc(requestBody));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarded.Should().NotBeNull();
        forwarded!.Method.Should().Be(HttpMethod.Post);
        forwarded.RequestUri!.AbsolutePath.Should().Be(path);
        forwardedBody.Should().Be(requestBody);
        response
            .Headers.GetValues("mcp-session-id")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("19d9ea76-c0b5-44bd-b554-f6e5e6127461");
    }

    [Fact]
    public async Task Session_id_and_protocol_version_headers_are_forwarded()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
            {
                forwarded = req;
                return Task.FromResult(TestUpstream.Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
            }
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/readonly") { Content = JsonRpc("{}") };
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "19d9ea76-c0b5-44bd-b554-f6e5e6127461");
        request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2025-06-18");

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        forwarded!
            .Headers.GetValues("Mcp-Session-Id")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("19d9ea76-c0b5-44bd-b554-f6e5e6127461");
        forwarded.Headers.GetValues("Mcp-Protocol-Version").Should().ContainSingle().Which.Should().Be("2025-06-18");
    }

    [Fact]
    public async Task X_mcp_control_headers_are_forwarded()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
            {
                forwarded = req;
                return Task.FromResult(TestUpstream.Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
            }
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/readonly") { Content = JsonRpc("{}") };
        request.Headers.TryAddWithoutValidation("X-MCP-Tools", "get_file_contents,search_code");
        request.Headers.TryAddWithoutValidation("X-MCP-Readonly", "true");
        request.Headers.TryAddWithoutValidation("X-MCP-Host", "copilot-cli");

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        forwarded!
            .Headers.GetValues("X-MCP-Tools")
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("get_file_contents,search_code");
        forwarded.Headers.GetValues("X-MCP-Readonly").Should().ContainSingle().Which.Should().Be("true");
        forwarded.Headers.GetValues("X-MCP-Host").Should().ContainSingle().Which.Should().Be("copilot-cli");
    }

    [Fact]
    public async Task Inbound_authorization_is_overridden_but_arbitrary_custom_headers_pass_through()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
            {
                forwarded = req;
                return Task.FromResult(TestUpstream.Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}"));
            }
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/readonly") { Content = JsonRpc("{}") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer inbound-token");
        request.Headers.TryAddWithoutValidation("x-api-key", "secret-key");
        request.Headers.TryAddWithoutValidation("X-Some-Future-Mcp-Header", "anything");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        // Authorization is force-set by CopilotHeadersHandler to the Copilot bearer, never the inbound value.
        forwarded!.Headers.Authorization!.Parameter.Should().NotBe("inbound-token");
        // Everything else passes through verbatim — no curated allowlist for MCP.
        forwarded.Headers.GetValues("x-api-key").Should().ContainSingle().Which.Should().Be("secret-key");
        forwarded.Headers.GetValues("X-Some-Future-Mcp-Header").Should().ContainSingle().Which.Should().Be("anything");
        forwarded
            .Headers.Contains("Accept-Encoding")
            .Should()
            .BeFalse(
                "upstream never negotiates compression and Content-Encoding is stripped on the way back, "
                    + "so forwarding this would risk an undecodable body"
            );
    }

    [Fact]
    public async Task Get_opens_a_standalone_stream_and_forwards_last_event_id()
    {
        HttpRequestMessage? forwarded = null;
        const string sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/ping\"}\n\n";
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
            {
                forwarded = req;
                return Task.FromResult(TestUpstream.Sse(sse));
            }
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", "42");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarded!.Method.Should().Be(HttpMethod.Get);
        forwarded.Content.Should().BeNull("GET carries no body");
        forwarded.Headers.GetValues("Last-Event-ID").Should().ContainSingle().Which.Should().Be("42");
        (await response.Content.ReadAsStringAsync()).Should().Be(sse);
    }

    [Fact]
    public async Task Delete_forwards_session_termination_and_the_upstream_status_code()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
            {
                forwarded = req;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/mcp/readonly");
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "19d9ea76-c0b5-44bd-b554-f6e5e6127461");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        forwarded!.Method.Should().Be(HttpMethod.Delete);
        forwarded.Content.Should().BeNull("DELETE carries no body");
    }

    [Fact]
    public async Task Unsupported_http_method_is_rejected_before_reaching_the_upstream()
    {
        await using var factory = new ProxyWebAppFactory(
            (req, ct) => throw new InvalidOperationException("upstream must not be called for an unsupported method")
        );
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/mcp/readonly") { Content = JsonRpc("{}") };
        using var response = await client.SendAsync(request);

        // Falls through to the shared Anthropic-shaped fallback 404, same as any other unmatched route/method.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
