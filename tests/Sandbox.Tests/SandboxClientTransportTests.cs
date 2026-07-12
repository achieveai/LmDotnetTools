using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientTransportTests
{
    [Fact]
    public async Task SendMcpToolCallAsync_ExactWireShape_MatchesJsonRpcContract()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/workspace/CLAUDE.md" });

        result.GetProperty("ok").GetBoolean().Should().BeTrue();

        var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        body.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        body.GetProperty("method").GetString().Should().Be("tools/call");
        body.GetProperty("params").GetProperty("name").GetString().Should().Be("Read");
        body.GetProperty("params").GetProperty("arguments").GetProperty("file_path").GetString().Should().Be("/workspace/CLAUDE.md");
    }

    [Fact]
    public async Task SendMcpToolCallAsync_StampsSessionIdAndAuthHeaders()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"result":{}}""");

        _ = await client.SendMcpToolCallAsync("sess-42", "Read", new { file_path = "/workspace/x" });

        var sent = handler.Requests.Single();
        sent.SessionId.Should().Be("sess-42");
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
    }

    [Fact]
    public async Task SendMcpToolCallAsync_JsonRpcErrorEnvelope_ThrowsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"session not found"}}""");

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task SendMcpToolCallAsync_JsonRpcErrorMessage_NeverLeaksIntoExceptionMessageToStringOrInnerException()
    {
        // error.message is gateway-controlled free text that can carry secrets (e.g. an upstream
        // tool echoing back credential material in its failure output) — it must never be copied
        // into the SandboxException, in either plain-decoded or base64-encoded form, and no inner
        // exception should carry it either.
        const string decodedSentinel = "sk-sandbox-super-secret-decoded-9f3a1c";
        var encodedSentinel = Convert.ToBase64String(Encoding.UTF8.GetBytes("sk-sandbox-super-secret-encoded-7c214e"));

        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/mcp",
            $$$"""{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"auth failed: token={{{decodedSentinel}}} blob={{{encodedSentinel}}}","data":"{{{encodedSentinel}}}"}}"""
        );

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().NotContain(decodedSentinel);
        sandboxException.Message.Should().NotContain(encodedSentinel);
        sandboxException.ToString().Should().NotContain(decodedSentinel);
        sandboxException.ToString().Should().NotContain(encodedSentinel);
        sandboxException.InnerException.Should().BeNull();
    }

    [Fact]
    public async Task SendMcpToolCallAsync_JsonRpcError_SurfacesOnlyTheNumericCode()
    {
        // The redacted message may safely include the JSON-RPC "code" (a small gateway-defined
        // integer, not caller-influenced free text) but must never include "message".
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"error":{"code":-32017,"message":"do not leak this text"}}""");

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        var sandboxException = (SandboxException)exception!;
        sandboxException.Message.Should().Contain("-32017");
        sandboxException.Message.Should().NotContain("do not leak this text");
    }

    [Fact]
    public async Task SendMcpToolCallAsync_JsonRpcErrorWithNonNumericCode_OmitsCodeSuffix_NeverThrowsRaw()
    {
        // A "code" that is not a JSON number (malformed/gateway bug) must not be surfaced as-is and
        // must not crash extraction — it is simply omitted from the redacted message.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"error":{"code":"not-a-number","message":"do not leak this either"}}""");

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().NotContain("do not leak this either");
    }

    [Fact]
    public async Task SendMcpToolCallAsync_JsonRpcErrorMissingCode_ThrowsProtocol_WithoutCodeSuffix()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"error":{"message":"session unreachable"}}""");

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        var sandboxException = (SandboxException)exception!;
        sandboxException.Kind.Should().Be(SandboxErrorKind.Protocol);
        sandboxException.Message.Should().NotContain("session unreachable");
    }

    [Theory]
    // Root is not a JSON object — reading a property off a non-object JsonElement would otherwise
    // throw a raw InvalidOperationException, not a SandboxException.
    [InlineData("[]")]
    [InlineData("\"a bare string\"")]
    [InlineData("42")]
    [InlineData("null")]
    // 'jsonrpc' missing / wrong version / not a string.
    [InlineData("{\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":2.0,\"id\":1,\"result\":{}}")]
    // 'id' missing / mismatched / wrong type (a compliant reply MUST echo the request id as a number).
    [InlineData("{\"jsonrpc\":\"2.0\",\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}")]
    // result/error mutual-exclusivity: neither present, or both present.
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"error\":{\"code\":-1,\"message\":\"x\"}}")]
    // 'error' present but not a JSON-RPC error object.
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":\"boom\"}")]
    public async Task SendMcpToolCallAsync_MalformedEnvelope_ThrowsProtocol_NeverRawException(string body)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", body);

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task SendMcpToolCallAsync_ResultCanBeAnyJsonValue_IncludingNull()
    {
        // JSON-RPC allows 'result' to be any value (including JSON null) as long as it is present and
        // 'error' is absent — the envelope is well-formed and the (null) result is returned as-is.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"result":null}""");

        var result = await client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" });

        result.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SendMcpToolCallAsync_NullErrorMemberWithResult_IsTreatedAsSuccess()
    {
        // An explicit `"error": null` alongside a result is not an error envelope — it must resolve to
        // the result, not be misclassified as a failure.
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"error":null,"result":{"ok":true}}""");

        var result = await client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" });

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Rest3xxRedirect_IsRejectedAsProtocol_AndNeverFollowed()
    {
        // The SDK must never follow a redirect: following one would replay the X-Sbx-* credential
        // headers to the redirect target. An observed 3xx is rejected as Protocol, and only the single
        // original request is ever sent (the Location is not chased).
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal),
            _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://malicious.invalid:9999/api/v1/sandboxes");
                return redirect;
            }
        );

        var exception = await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws")));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
        ((SandboxException)exception!).StatusCode.Should().Be(302);
        handler.Requests.Should().ContainSingle();
        handler.Requests.Single().Uri.Host.Should().NotBe("malicious.invalid");
    }

    [Fact]
    public async Task Mcp3xxRedirect_IsRejectedAsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.On(
            req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal),
            _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri("http://malicious.invalid:9999/mcp");
                return redirect;
            }
        );

        var exception = await Record.ExceptionAsync(() => client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/x" }));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.Protocol);
        ((SandboxException)exception!).StatusCode.Should().Be(307);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task IsHealthyAsync_SendsNoCredentialHeaders()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/health", HttpStatusCode.OK);

        var healthy = await client.IsHealthyAsync();

        healthy.Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.SbxAppId.Should().BeNull();
        sent.SbxAppKey.Should().BeNull();
    }

    [Fact]
    public async Task IsHealthyAsync_UnreachableGateway_ReturnsFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/health", HttpStatusCode.ServiceUnavailable);

        var healthy = await client.IsHealthyAsync();

        healthy.Should().BeFalse();
    }

    [Fact]
    public async Task TransportTimeout_DistinctFromCallerCancellation_ThrowsSandboxException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: TimeSpan.FromMilliseconds(50));
        handler.OnHang(r => r.Method == HttpMethod.Post);

        var exception = await Record.ExceptionAsync(() => client.CreateAsync(new SandboxCreateRequest("ws")));

        exception.Should().BeOfType<SandboxException>();
        ((SandboxException)exception!).Kind.Should().Be(SandboxErrorKind.TransportTimeout);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceledException_NotSandboxException()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient(transportTimeout: TimeSpan.FromSeconds(30));
        handler.OnHang(r => r.Method == HttpMethod.Post);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.CreateAsync(new SandboxCreateRequest("ws"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OwnedClient_Dispose_DisposesTransport()
    {
        var options = new SandboxClientOptions(
            TestSupport.NewLoopbackAddress(),
            "app-1",
            TestSupport.ValidSecret,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30)
        );
        var client = new SandboxClient(options);
        client.OwnsTransport.Should().BeTrue();
        var transport = client.Transport;

        client.Dispose();

        var act = () => transport.GetAsync("health");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task BorrowedClient_Dispose_LeavesHttpClientUsable()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/health", HttpStatusCode.OK);
        client.OwnsTransport.Should().BeFalse();
        var httpClient = client.Transport;

        client.Dispose();

        var response = await httpClient.GetAsync("health");
        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_NeverIssuesDeleteRequest()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        _ = await client.CreateAsync(new SandboxCreateRequest("ws"));
        client.Dispose();

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (client, _) = TestSupport.CreateBorrowedClient();

        client.Dispose();
        var act = client.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task CreateAsync_BorrowedClientWithNullBaseAddress_StillReachesConfiguredServerAddress()
    {
        // A borrowed HttpClient with no BaseAddress would make `new HttpRequestMessage(method,
        // "relative/path")` throw InvalidOperationException when HttpClient tries (and fails) to
        // combine it with a null BaseAddress. The SDK must never depend on the borrowed client's
        // BaseAddress at all — every request is resolved against the validated
        // SandboxClientOptions.ServerAddress regardless.
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(httpClientBaseAddress: null);
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.SessionId.Should().Be("sess-1");
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
        sent.Uri.Port.Should().Be(serverAddress.Port);
    }

    [Fact]
    public async Task CreateAsync_BorrowedClientWithMismatchedBaseAddress_RoutesToServerAddress_NotBorrowedBaseAddress()
    {
        // A borrowed client's BaseAddress could point at an entirely different (e.g. malicious or
        // stale) host. The SDK must ignore it and always send to the configured ServerAddress — and
        // therefore never hand X-Sbx-App-Id/X-Sbx-App-Key to the mismatched host.
        var maliciousBaseAddress = new Uri("http://malicious.invalid:9999");
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(maliciousBaseAddress);
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        info.SessionId.Should().Be("sess-1");
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
        sent.Uri.Host.Should().NotBe(maliciousBaseAddress.Host);
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
    }

    [Fact]
    public async Task IsHealthyAsync_BorrowedClientWithNullBaseAddress_StillReachesConfiguredServerAddress()
    {
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(httpClientBaseAddress: null);
        handler.OnStatus(HttpMethod.Get, "/health", HttpStatusCode.OK);

        var healthy = await client.IsHealthyAsync();

        healthy.Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
    }

    [Fact]
    public async Task IsHealthyAsync_BorrowedClientWithMismatchedBaseAddress_RoutesToServerAddress_WithoutCredentials()
    {
        var maliciousBaseAddress = new Uri("http://malicious.invalid:9999");
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(maliciousBaseAddress);
        handler.OnStatus(HttpMethod.Get, "/health", HttpStatusCode.OK);

        var healthy = await client.IsHealthyAsync();

        healthy.Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
        sent.Uri.Host.Should().NotBe(maliciousBaseAddress.Host);
        sent.SbxAppId.Should().BeNull();
        sent.SbxAppKey.Should().BeNull();
    }

    [Fact]
    public async Task SendMcpToolCallAsync_BorrowedClientWithNullBaseAddress_StillReachesConfiguredServerAddress()
    {
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(httpClientBaseAddress: null);
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/workspace/x" });

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
    }

    [Fact]
    public async Task SendMcpToolCallAsync_BorrowedClientWithMismatchedBaseAddress_RoutesToServerAddress()
    {
        var maliciousBaseAddress = new Uri("http://malicious.invalid:9999");
        var (client, handler, serverAddress) = TestSupport.CreateBorrowedClientWithBaseAddress(maliciousBaseAddress);
        handler.OnJson(HttpMethod.Post, "/mcp", """{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        var result = await client.SendMcpToolCallAsync("sess-1", "Read", new { file_path = "/workspace/x" });

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        var sent = handler.Requests.Single();
        sent.Uri.Host.Should().Be(serverAddress.Host);
        sent.Uri.Host.Should().NotBe(maliciousBaseAddress.Host);
        sent.SbxAppId.Should().Be("app-1");
        sent.SbxAppKey.Should().Be(TestSupport.ValidSecret);
    }
}
