using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientEnvTests
{
    [Fact]
    public async Task GetEnvAsync_HappyPath_ParsesEnvMap()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/env", """{"env":{"FOO":"1","BAR":"x"}}""");

        var env = await client.GetEnvAsync("sess-1");

        env.Should().Equal(new Dictionary<string, string> { ["FOO"] = "1", ["BAR"] = "x" });
    }

    [Fact]
    public async Task GetEnvAsync_RequestCarriesSessionId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/env", """{"env":{"FOO":"1"}}""");

        _ = await client.GetEnvAsync("sess-1");

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Get);
        sent.SessionId.Should().Be("sess-1");
        sent.Uri.AbsolutePath.Should().Be("/api/v1/sandboxes/sess-1/env");
    }

    [Fact]
    public async Task GetEnvAsync_NullEnvField_ReturnsEmptyMap()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/env", """{"env":null}""");

        var env = await client.GetEnvAsync("sess-1");

        env.Should().BeEmpty();
    }

    [Fact]
    public async Task GetEnvAsync_404_MapsToNotFound()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/env",
            """{"error":"session not found","error_code":"session_not_found"}""",
            HttpStatusCode.NotFound
        );

        var exception = await Record.ExceptionAsync(() => client.GetEnvAsync("missing"));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.NotFound);
    }

    [Fact]
    public async Task PatchEnvAsync_SendsFlatBody_WithNullPreservedForUnset()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, "/env", """{"env":{"FOO":"2"}}""");

        _ = await client.PatchEnvAsync("sess-1", new Dictionary<string, string?> { ["FOO"] = "2", ["OLD"] = null });

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        var body = JsonDocument.Parse(sent.Body!).RootElement;

        body.TryGetProperty("env", out _).Should().BeFalse("PATCH body is flat, not wrapped in an env object");
        body.GetProperty("FOO").GetString().Should().Be("2");
        body.GetProperty("OLD").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PatchEnvAsync_ReturnsResultingMap()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, "/env", """{"env":{"FOO":"2","BAR":"x"}}""");

        var result = await client.PatchEnvAsync("sess-1", new Dictionary<string, string?> { ["FOO"] = "2" });

        result.Should().Equal(new Dictionary<string, string> { ["FOO"] = "2", ["BAR"] = "x" });
    }

    [Fact]
    public async Task PatchEnvAsync_InvalidEnv400_MapsToInvalidEnvWithKeys()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/env",
            """{"error":"invalid env keys","error_code":"invalid_env","keys":["HTTP_PROXY","bad-key"]}""",
            HttpStatusCode.BadRequest
        );

        var exception = await Record.ExceptionAsync(() =>
            client.PatchEnvAsync("sess-1", new Dictionary<string, string?> { ["HTTP_PROXY"] = "x" })
        );

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.InvalidEnv);
        sandboxException.InvalidKeys.Should().Equal("HTTP_PROXY", "bad-key");
    }
}
