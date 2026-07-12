using System.Net;
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
}
