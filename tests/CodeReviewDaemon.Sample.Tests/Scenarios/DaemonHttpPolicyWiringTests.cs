using System.Net.Http.Headers;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// PR #121 H2 (HTTP half) — the daemon's actual DI wiring must scope the outbound provider HTTP client
/// to the allow-listed repos, not just the provider host. With <c>acme/widgets</c> enabled, an
/// API call to that repo's route is permitted but an off-repo API path (a sibling repo on the same host)
/// is denied AND its credential withheld. Driven through the real <c>Program</c> graph (not a hand-built
/// policy) so a regression in the wiring is caught.
/// </summary>
public sealed class DaemonHttpPolicyWiringTests
{
    [Fact]
    public async Task The_registered_github_client_allows_an_enabled_repo_route()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:EnabledRepos:0", "acme/widgets"));

        using var client = configured.Services
            .GetRequiredService<PolicyEnforcedHttpClientFactory>()
            .Create("github");

        using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.github.com/repos/acme/widgets/pulls?state=open")
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok");

        // The inner handler will fail to actually reach the network, but the policy must NOT deny it.
        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().NotThrowAsync<OperationDeniedException>(
            "the enabled repo's own API route is in scope");
    }

    [Fact]
    public async Task The_registered_github_client_denies_an_off_repo_api_path()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:EnabledRepos:0", "acme/widgets"));

        using var client = configured.Services
            .GetRequiredService<PolicyEnforcedHttpClientFactory>()
            .Create("github");

        using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://api.github.com/repos/acme/secret-repo/pulls")
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok");

        var act = () => client.SendAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<OperationDeniedException>(
            "a repo that is not on the allow-list must be denied at the HTTP seam");
        request.Headers.Authorization.Should().BeNull("a denied operation withholds the credential");
    }
}
