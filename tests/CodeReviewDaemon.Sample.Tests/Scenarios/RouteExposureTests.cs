using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The daemon's runtime HTTP surface is the two gateway callbacks plus the release health and admission
/// control plane. The daemon does its PR-watching by polling and runs all git/fs work in the sandbox, so no
/// review execution route is exposed. This test enumerates the mapped endpoints and fails on accidental
/// surface growth.
/// </summary>
public sealed class RouteExposureTests
{
    private const string WebhookPattern = "api/auth/webhook/{provider}";
    private const string DiscoveryPattern = "api/discovery/context_discovery";
    private const string VersionPattern = "/health/version";

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

        patterns.Should().BeEquivalentTo([WebhookPattern, DiscoveryPattern, VersionPattern]);
    }

    [Fact]
    public async Task Network_admission_mutation_routes_do_not_exist()
    {
        using var factory = new DaemonWebAppFactory();
        using var client = factory.CreateClient();

        (await client.PostAsync("/health/admission/activate", null))
            .StatusCode.Should()
            .Be(System.Net.HttpStatusCode.NotFound);
        (await client.PostAsync("/health/admission/drain", null))
            .StatusCode.Should()
            .Be(System.Net.HttpStatusCode.NotFound);
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
