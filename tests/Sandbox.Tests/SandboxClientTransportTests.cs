using System.Net;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxClientTransportTests
{
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
}
