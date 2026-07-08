using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Wiring tests for LmStreaming's sandbox gateway app-auth (ADR 0029): that <c>AppKey</c> binds from the
/// <c>SandboxGateway</c> config section, and that a gateway REST client built the production way (its
/// <see cref="HttpClient"/> wrapped in a <see cref="GatewayAuthHandler"/>) actually emits the two bearer
/// headers on its outbound calls — verified through the real <see cref="MarketplaceCatalogClient"/> path.
/// </summary>
public class GatewayAppAuthWiringTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string EmptyCatalog = """{"selected":[],"marketplaces":[]}""";

    [Fact]
    public void AppKey_binds_from_the_SandboxGateway_config_section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SandboxGateway:AppId"] = "lmstreaming-sample",
                ["SandboxGateway:AppKey"] = "c2VjcmV0",
            })
            .Build();

        var options = config.GetSection(SandboxGatewayOptions.SectionName).Get<SandboxGatewayOptions>();

        options!.AppId.Should().Be("lmstreaming-sample");
        options.AppKey.Should().Be("c2VjcmV0");
    }

    [Fact]
    public async Task Catalog_client_sends_both_bearer_headers_when_configured()
    {
        var capture = new CapturingHandler(EmptyCatalog);
        var http = new HttpClient(new GatewayAuthHandler("lmstreaming-sample", "c2VjcmV0") { InnerHandler = capture });
        var client = new MarketplaceCatalogClient(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, AppId = "lmstreaming-sample", AppKey = "c2VjcmV0" },
            http,
            NullLogger<MarketplaceCatalogClient>.Instance);

        _ = await client.GetCatalogAsync();

        capture.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppIdHeader).Should().ContainSingle()
            .Which.Should().Be("lmstreaming-sample");
        capture.LastRequest!.Headers.GetValues(GatewayAuthHeaders.AppKeyHeader).Should().ContainSingle()
            .Which.Should().Be("c2VjcmV0");
    }

    [Fact]
    public async Task Catalog_client_sends_no_bearer_headers_when_key_is_unset()
    {
        var capture = new CapturingHandler(EmptyCatalog);
        var http = new HttpClient(new GatewayAuthHandler("lmstreaming-sample", appKey: null) { InnerHandler = capture });
        var client = new MarketplaceCatalogClient(
            new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
            http,
            NullLogger<MarketplaceCatalogClient>.Instance);

        _ = await client.GetCatalogAsync();

        capture.LastRequest!.Headers.Contains(GatewayAuthHeaders.AppIdHeader).Should().BeFalse();
        capture.LastRequest!.Headers.Contains(GatewayAuthHeaders.AppKeyHeader).Should().BeFalse();
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
