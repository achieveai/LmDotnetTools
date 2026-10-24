using System.Net;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Plugin MCP server descriptors (gateway issue #224, <c>Docs/tools-and-api.md</c> "GET …/mcp-servers"): the rows as
/// authored, the documented errors, and names that never print a url or a header value.
/// </summary>
public sealed class SandboxClientMcpServersTests
{
    // The documented example response.
    private const string DocsJson = """
        {"session_id":"abc-123","mcp_servers":[
          {"name":"api","plugin":"acme","marketplace":"official","type":"http","supported":true,"reason":null,
           "url":"https://api.acme.example/mcp",
           "headers":[{"name":"Authorization","value":"Bearer ${ACME_TOKEN}","placeholder":null},
                      {"name":"X-Api-Key","value":"${ACME_API_KEY}","placeholder":"ACME_API_KEY"}],
           "command":null,"args":[],"env":{},"extra":{"oauth":{"clientId":"acme-client"}}},
          {"name":"events","plugin":"acme","marketplace":"official","type":"sse","supported":true,"reason":null,
           "url":"https://events.acme.example/sse",
           "headers":[{"name":"X-Token","value":"${EVENTS_TOKEN}","placeholder":"EVENTS_TOKEN"}],
           "command":null,"args":[],"env":{},"extra":{}},
          {"name":"local","plugin":"acme","marketplace":"official","type":"stdio","supported":false,
           "reason":"stdio servers are not supported","url":null,"headers":[],
           "command":"npx","args":["-y","@acme/mcp"],"env":{"ACME_TOKEN":"${ACME_TOKEN}"},"extra":{}}]}
        """;

    [Fact]
    public async Task GetMcpServersAsync_MapsTheDocumentedRows_AsAuthored()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/abc-123/mcp-servers", DocsJson);

        var servers = await client.GetMcpServersAsync("abc-123");

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        servers.Select(s => s.Name).Should().Equal("api", "events", "local");
        var api = servers[0];
        (api.Plugin, api.Marketplace, api.Type, api.Supported, api.Reason, api.Url)
            .Should()
            .Be(("acme", "official", "http", true, null, "https://api.acme.example/mcp"));
        api.Headers.Select(h => (h.Name, h.Value, h.Placeholder))
            .Should()
            .Equal(("Authorization", "Bearer ${ACME_TOKEN}", null), ("X-Api-Key", "${ACME_API_KEY}", "ACME_API_KEY"));
        // Read after the response is gone: the mapper keeps its own copy of each extra value.
        api.Extra["oauth"].GetProperty("clientId").GetString().Should().Be("acme-client");
        var local = servers[2];
        (local.Type, local.Supported, local.Reason, local.Url)
            .Should()
            .Be(("stdio", false, "stdio servers are not supported", null));
        local.Headers.Should().BeEmpty();
        local.Extra.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":"session not found","code":404}""", SandboxErrorKind.NotFound)]
    [InlineData(
        HttpStatusCode.ServiceUnavailable,
        """{"error":"retry","code":503,"error_code":"marketplaces_unavailable","retryable":true}""",
        SandboxErrorKind.Unavailable
    )]
    public async Task GetMcpServersAsync_DocumentedErrors_MapToTheirKinds(
        HttpStatusCode status,
        string body,
        SandboxErrorKind expected
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/abc-123/mcp-servers", body, status);

        var thrown = (await Record.ExceptionAsync(() => client.GetMcpServersAsync("abc-123")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(expected);
    }

    /// <summary>A row without its supported flag is a protocol error whose message carries none of the row's values.</summary>
    [Fact]
    public async Task GetMcpServersAsync_ARowWithoutSupported_IsAProtocolError_ThatNamesNoValue()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/abc-123/mcp-servers",
            """
            {"session_id":"abc-123","mcp_servers":[{"name":"api","plugin":"acme","marketplace":"official",
             "type":"http","url":"https://secret-host.example/mcp",
             "headers":[{"name":"Authorization","value":"Bearer secret-value","placeholder":null}],"extra":{}}]}
            """
        );

        var thrown = (await Record.ExceptionAsync(() => client.GetMcpServersAsync("abc-123")))
            .Should()
            .BeOfType<SandboxException>()
            .Subject;

        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.ToString().Should().NotContain("secret-host").And.NotContain("secret-value");
    }

    /// <summary>A row is logged by name: its <c>ToString</c> never prints the url or a header value.</summary>
    [Fact]
    public async Task ToString_NamesTheServerAndTheHeader_NeverTheUrlOrAValue()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/abc-123/mcp-servers", DocsJson);

        var api = (await client.GetMcpServersAsync("abc-123"))[0];

        api.ToString().Should().Be("official/acme/api (http)");
        api.Headers[0].ToString().Should().Be("Authorization");
    }
}
