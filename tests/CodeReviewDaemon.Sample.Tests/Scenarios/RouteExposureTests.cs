using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// AC#4: the daemon's runtime HTTP surface is <strong>exactly</strong>
/// <c>POST /api/auth/webhook/{provider}</c> — the sandbox gateway's post-auth callback — and nothing
/// else. The daemon does its PR-watching by polling and runs all git/fs work in the sandbox, so it
/// deliberately exposes no other endpoints. This test enumerates the host's mapped endpoints and
/// fails if any route other than the webhook is reachable.
/// </summary>
public sealed class RouteExposureTests
{
    private const string WebhookPattern = "api/auth/webhook/{provider}";

    [Fact]
    public void Only_the_auth_webhook_route_is_mapped()
    {
        using var factory = new DaemonWebAppFactory();

        // Accessing Services forces the host to build and the endpoints to be composed.
        var endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.Should().NotBeEmpty("the auth webhook route must be mapped");

        var patterns = endpoints
            .Select(e => e.RoutePattern.RawText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        patterns.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(WebhookPattern);
    }

    [Fact]
    public void The_webhook_route_only_accepts_POST()
    {
        using var factory = new DaemonWebAppFactory();

        var webhook = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(
                e.RoutePattern.RawText,
                WebhookPattern,
                StringComparison.OrdinalIgnoreCase));

        var httpMethods = webhook.Metadata
            .GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>();

        httpMethods.Should().NotBeNull();
        httpMethods!.HttpMethods.Should().BeEquivalentTo(["POST"]);
    }
}
