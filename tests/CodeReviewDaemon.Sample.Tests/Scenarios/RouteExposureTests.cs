using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// AC#4: the daemon's runtime HTTP surface is <strong>exactly</strong> two gateway callbacks —
/// <c>POST /api/auth/webhook/{provider}</c> (post-auth callback) and
/// <c>POST /api/discovery/context_discovery</c> (context-discovery callback, which must return 2xx or the
/// gateway tears down the sandbox session) — and nothing else. The daemon does its PR-watching by polling
/// and runs all git/fs work in the sandbox, so it deliberately exposes no other endpoints. This test
/// enumerates the host's mapped endpoints and fails if any route other than those two is reachable.
/// </summary>
public sealed class RouteExposureTests
{
    private const string WebhookPattern = "api/auth/webhook/{provider}";
    private const string DiscoveryPattern = "api/discovery/context_discovery";

    [Fact]
    public void Only_the_two_gateway_callback_routes_are_mapped()
    {
        using var factory = new DaemonWebAppFactory();

        // Accessing Services forces the host to build and the endpoints to be composed.
        var endpoints = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        endpoints.Should().NotBeEmpty("the gateway callback routes must be mapped");

        var patterns = endpoints
            .Select(e => e.RoutePattern.RawText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        patterns.Should().BeEquivalentTo([WebhookPattern, DiscoveryPattern]);
    }

    [Fact]
    public void Audit_routes_are_mapped_only_when_ingestion_is_enabled()
    {
        using var factory = new DaemonWebAppFactory(
            enableReviewAuditIngestion: true,
            reviewBridgeSecret: "route-exposure-secret"
        );

        var patterns = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        patterns
            .Should()
            .BeEquivalentTo([
                WebhookPattern,
                DiscoveryPattern,
                "api/review-audit/records/{recordId}",
                "api/review-audit/records/{recordId}/chunks/{chunkIndex}",
                "api/review-audit/records/{recordId}/complete",
                "api/review-audit/rounds/{roundId:long}/status",
            ]);
    }

    [Fact]
    public void Publication_route_is_mapped_only_when_typed_publication_is_enabled()
    {
        using var factory = new DaemonWebAppFactory(
            reviewBridgeSecret: "route-exposure-secret",
            enableTypedReviewPublication: true
        );

        var patterns = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        patterns
            .Should()
            .BeEquivalentTo([
                WebhookPattern,
                DiscoveryPattern,
                "api/review-publication/rounds/{roundId:long}/actions/{operation}",
            ]);
    }

    [Theory]
    [InlineData(WebhookPattern)]
    [InlineData(DiscoveryPattern)]
    public void Each_gateway_callback_route_only_accepts_POST(string pattern)
    {
        using var factory = new DaemonWebAppFactory();

        var route = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, pattern, StringComparison.OrdinalIgnoreCase));

        var httpMethods = route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>();

        httpMethods.Should().NotBeNull();
        httpMethods!.HttpMethods.Should().BeEquivalentTo(["POST"]);
    }
}
