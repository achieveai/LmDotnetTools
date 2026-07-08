using System.Net;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Tests for <see cref="GatewayAuthHandler"/> — the REST-path <see cref="DelegatingHandler"/> that attaches
/// the gateway's per-app bearer headers. It attaches both headers when configured, neither when the key is
/// absent (so an unenforced gateway is unaffected), and never overwrites a header the caller already set.
/// </summary>
public class GatewayAuthHandlerTests
{
    private static (HttpClient Client, CapturingHandler Inner) BuildClient(string? appId, string? appKey)
    {
        var inner = new CapturingHandler();
        var handler = new GatewayAuthHandler(appId, appKey) { InnerHandler = inner };
        return (new HttpClient(handler), inner);
    }

    [Fact]
    public async Task Attaches_both_bearer_headers_when_configured()
    {
        var (client, inner) = BuildClient("code-review-daemon", "c2VjcmV0");

        _ = await client.GetAsync("http://localhost:3000/api/v1/sandboxes");

        inner.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppIdHeader).Should().ContainSingle()
            .Which.Should().Be("code-review-daemon");
        inner.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppKeyHeader).Should().ContainSingle()
            .Which.Should().Be("c2VjcmV0");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("code-review-daemon", null)]
    [InlineData("code-review-daemon", "")]
    public async Task Attaches_no_bearer_headers_when_key_is_missing(string? appId, string? appKey)
    {
        var (client, inner) = BuildClient(appId, appKey);

        _ = await client.GetAsync("http://localhost:3000/api/v1/sandboxes");

        inner.LastRequest!.Headers.Contains(GatewayAuthHeaders.AppIdHeader).Should().BeFalse();
        inner.LastRequest!.Headers.Contains(GatewayAuthHeaders.AppKeyHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_overwrite_a_header_the_caller_already_set()
    {
        var (client, inner) = BuildClient("code-review-daemon", "c2VjcmV0");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:3000/api/v1/sandboxes");
        _ = request.Headers.TryAddWithoutValidation(GatewayAuthHeaders.AppIdHeader, "preset-app");

        _ = await client.SendAsync(request);

        inner.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppIdHeader).Should().ContainSingle()
            .Which.Should().Be("preset-app");
        inner.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppKeyHeader).Should().ContainSingle()
            .Which.Should().Be("c2VjcmV0");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
