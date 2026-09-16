using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

    /// <summary>
    /// An error response from something OTHER than the gateway — a reverse proxy, a load balancer, a
    /// captive portal — carries that thing's own body and headers. A legacy proxy labelling its error
    /// page <c>text/html; charset=windows-1252</c> makes <c>ReadFromJsonAsync</c> throw
    /// <see cref="InvalidOperationException"/> on the charset BEFORE any parsing happens, so a catch
    /// keyed on <see cref="JsonException"/> alone never saw it. Every caller of this SDK catches
    /// <see cref="SandboxException"/>; the bare exception walked past all of them, and on the env routes
    /// that also means the registry's old-gateway degradation never runs and the session faults.
    /// <para>
    /// The charset, not the media type, is what matters here: a <c>text/html</c> body with a known
    /// charset still reaches the parser and fails as a <see cref="JsonException"/>, which was already
    /// handled. That is the case the sibling test below pins, so this pair also documents the difference.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    public async Task EnvError_WithUnknownCharset_StillSurfacesAsSandboxException(string verb)
    {
        var exception = await CaptureEnvErrorAsync(verb, "windows-1252");

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.StatusCode.Should().Be(502);
        // Unreadable body means no error_code, so it classifies as Protocol — NOT as invalid_env, and
        // not as the "route is absent" shape the registry keys its old-gateway degradation on.
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.ErrorCode.Should().BeNull();
    }

    /// <summary>The already-handled half: an HTML body the parser can at least read. See above.</summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("PATCH")]
    public async Task EnvError_WithHtmlBody_StillSurfacesAsSandboxException(string verb)
    {
        var exception = await CaptureEnvErrorAsync(verb, "utf-8");

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    /// <summary>Drives one env call against a 502 whose body is a proxy's HTML error page labelled with
    /// <paramref name="charset"/>, and returns whatever came out.</summary>
    private static async Task<Exception?> CaptureEnvErrorAsync(string verb, string charset)
    {
        var method = verb == "GET" ? HttpMethod.Get : HttpMethod.Patch;
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            req =>
                req.Method == method
                && req.RequestUri is not null
                && req.RequestUri.AbsolutePath.EndsWith("/env", StringComparison.Ordinal),
            _ =>
            {
                var content = new ByteArrayContent(
                    Encoding.UTF8.GetBytes("<html><body><h1>502 Bad Gateway</h1></body></html>")
                );
                content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset };
                return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content };
            }
        );

        return await Record.ExceptionAsync(() =>
            method == HttpMethod.Get
                ? client.GetEnvAsync("sess-1")
                : client.PatchEnvAsync("sess-1", new Dictionary<string, string?> { ["FOO"] = "1" })
        );
    }
}
