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

    /// <summary>
    /// Issue #491's acceptance is "refuses startup", not "a validator exists". This pins the WIRING: the
    /// malformed entry has to reach <c>PrPollTargetBuilder.ValidateEnabledRepos</c> through the real
    /// <c>Program</c> graph and stop the host coming up. Deleting the call from <c>Program.cs</c> leaves the
    /// validator's own unit tests green — only booting the host here turns that mutation red.
    /// <para>
    /// <c>owner//repo</c> is the distinguishing fixture: <c>Build</c>'s <c>RemoveEmptyEntries</c> split reads
    /// it as the 2-segment <c>owner/repo</c> and polls a DIFFERENT repo, so without validation the daemon
    /// starts happily and silently watches the wrong thing.
    /// </para>
    /// </summary>
    [Fact]
    public void The_host_refuses_to_start_when_an_enabled_repo_entry_is_malformed()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:EnabledRepos:0", "owner//repo"));

        // Resolving anything from the graph forces the host to build, which runs Program's configuration phase.
        var act = () => configured.Services.GetRequiredService<PolicyEnforcedHttpClientFactory>();

        act.Should()
            .Throw<InvalidOperationException>("startup must refuse a malformed EnabledRepos entry")
            .WithMessage("*owner//repo*", "the refusal must name the offending entry");
    }
}
