using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P2.3 — host wiring: the daemon resolves auth through the fail-fast policy (not the chat app's
/// deferred-hold coordinator), and Azure DevOps is opt-in. With the conservative default
/// (<c>EnableAdoProvider = false</c>) only the GitHub provider is registered, so an <c>ado</c> webhook
/// call finds no provider and is denied as unknown; enabling the flag registers both.
/// </summary>
public sealed class DaemonAuthRegistrationTests
{
    [Fact]
    public void The_daemon_resolves_the_fail_fast_auth_policy()
    {
        using var factory = new DaemonWebAppFactory();

        var policy = factory.Services.GetRequiredService<IAuthResolutionPolicy>();

        policy.Should().BeOfType<FailFastDaemonAuthPolicy>(
            "the unattended daemon fails fast rather than holding the webhook call open for a human");
    }

    [Fact]
    public void GitHub_only_by_default_so_ado_has_no_registered_provider()
    {
        using var factory = new DaemonWebAppFactory();

        var providerIds = factory.Services
            .GetServices<IOAuthTokenProvider>()
            .Select(p => p.ProviderId);

        providerIds.Should().BeEquivalentTo(["github"], "ADO is opt-in via EnableAdoProvider");
    }

    [Fact]
    public void Enabling_the_ado_flag_registers_the_ado_provider()
    {
        using var factory = new DaemonWebAppFactory();
        using var adoEnabled = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:EnableAdoProvider", "true"));

        var providerIds = adoEnabled.Services
            .GetServices<IOAuthTokenProvider>()
            .Select(p => p.ProviderId);

        providerIds.Should().BeEquivalentTo(["github", "ado"]);
    }
}
