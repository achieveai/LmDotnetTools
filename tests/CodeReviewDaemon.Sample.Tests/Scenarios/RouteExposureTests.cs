using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>Only authenticated gateway callbacks and the scoped workflow publication callback are exposed.</summary>
public sealed class RouteExposureTests
{
    private const string WebhookPattern = "api/auth/webhook/{provider}";
    private const string DiscoveryPattern = "api/discovery/context_discovery";
    private const string PublicationPattern = "api/workflow/publication";

    [Fact]
    public void Only_the_gateway_and_workflow_callback_routes_are_mapped()
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

        patterns.Should().BeEquivalentTo([WebhookPattern, DiscoveryPattern, PublicationPattern]);
    }

    [Theory]
    [InlineData(WebhookPattern)]
    [InlineData(DiscoveryPattern)]
    [InlineData(PublicationPattern)]
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

    [Fact]
    public async Task Publication_callback_rejects_an_unconfigured_or_missing_credential()
    {
        using var factory = new DaemonWebAppFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/workflow/publication", new StringContent("{}"));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
