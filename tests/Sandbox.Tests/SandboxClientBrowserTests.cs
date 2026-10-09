using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public sealed class SandboxClientBrowserTests
{
    [Fact]
    public async Task BrowserCall_PreservesImageAndInstance_AndSuppliesOnlyTheBoundBrowserId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/mcp",
            """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"image","mimeType":"image/png","data":"aW1hZ2U="}],"_meta":{"sbx/browser_id":"owned-browser","sbx/browser_instance":"instance-1"}}}"""
        );
        using var args = JsonDocument.Parse("""{"url":"https://preview.sbx.invalid/report.html"}""");

        var result = await client.CallBrowserAsync("owned-browser", "playwright", "browser_navigate", args.RootElement);

        result.GetProperty("content")[0].GetProperty("data").GetString().Should().Be("aW1hZ2U=");
        result.GetProperty("_meta").GetProperty("sbx/browser_instance").GetString().Should().Be("instance-1");
        var request = handler.Requests.Should().ContainSingle().Subject;
        using var wire = JsonDocument.Parse(request.Body!);
        var parameters = wire.RootElement.GetProperty("params");
        parameters.GetProperty("name").GetString().Should().Be("Browser");
        parameters.GetProperty("arguments").GetProperty("browser_id").GetString().Should().Be("owned-browser");
        parameters
            .GetProperty("arguments")
            .GetProperty("arguments")
            .GetProperty("url")
            .GetString()
            .Should()
            .Be("https://preview.sbx.invalid/report.html");
        request.SbxAppId.Should().Be("app-1");
        request.SbxAppKey.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("{\"tools\":[{\"name\":\"Browser\"}]}", true)]
    [InlineData("{\"tools\":[{\"name\":\"Bash\"}]}", false)]
    [InlineData("{\"tools\":[null,{\"name\":7},\"Browser\",{\"name\":\"Browser\"}]}", true)]
    public async Task BrowserSupport_UsesAdvertisedToolsWithoutCreatingALease(string response, bool expected)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/mcp/tools", response);
        (await client.SupportsBrowserAsync()).Should().Be(expected);
        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task BrowserCall_RefusesAResponseLargerThanTheBrowserCapBeforeParsing()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", new string(' ', (int)SandboxClient.MaxBrowserResponseBytes + 1));

        var act = () => client.CallBrowserAsync("owned-browser", "playwright");

        (await act.Should().ThrowAsync<SandboxException>()).Which.IsDirectReadCapExceeded.Should().BeTrue();
    }

    [Fact]
    public async Task BrowserCall_JsonRpcErrorEnvelope_SurfacesCodeAndMessage()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // Shape of the gateway's McpResponse::error (server.rs): a JSON-RPC error with no result.
        handler.OnJson(
            HttpMethod.Post,
            "/mcp",
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Invalid agent_id: must be 1-64 characters"}}"""
        );

        var act = () => client.CallBrowserAsync("owned-browser", "playwright", "browser_snapshot");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.McpCallFailed);
        thrown.JsonRpcCode.Should().Be(-32602);
        thrown.Message.Should().Contain("Invalid agent_id: must be 1-64 characters");
    }

    [Fact]
    public async Task CloseBrowser_IdempotentNoContent_AddressesOnlyTheNamedBrowser()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Delete, "/api/v1/browsers/owned-browser", HttpStatusCode.NoContent);

        await client.CloseBrowserAsync("owned-browser");
        await client.CloseBrowserAsync("owned-browser");

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Should().AllSatisfy(r => r.Uri.AbsolutePath.Should().Be("/api/v1/browsers/owned-browser"));
    }

    [Fact]
    public async Task CloseBrowser_NotFoundMeansBrowsersUnsupported_AndThrows()
    {
        // relay.rs scope(): DELETE answers 404 only when the gateway has no browser manager.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Delete,
            "/api/v1/browsers/owned-browser",
            """{"error":"x","code":404,"error_code":"browser_unsupported","retryable":false}""",
            HttpStatusCode.NotFound
        );

        var act = () => client.CloseBrowserAsync("owned-browser");

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.BrowserUnsupported);
        thrown.ErrorCode.Should().Be("browser_unsupported");
    }
}
