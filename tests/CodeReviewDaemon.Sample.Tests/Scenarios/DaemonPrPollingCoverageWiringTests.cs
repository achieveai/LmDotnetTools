using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #537 — <c>CodeReviewDaemon:MaxPagesPerPoll</c> was declared and documented with <b>zero readers</b>:
/// both PR providers ignored it in favour of a private <c>const int MaxPages = 10</c>, so an operator who set
/// it would have changed nothing. (No shipped profile does set it — the knob was documented, not used.)
/// Measured consequence: ~101 of 711 active PRs enumerated per poll.
/// <para>
/// These tests pin the half a provider-level test cannot: that the operator's configured value actually
/// reaches the provider instance the host registers. The provider tests prove the bound changes how many
/// pages are fetched; deleting the argument from <c>Program.cs</c> leaves every one of them green, because
/// they construct the provider themselves. Only booting the real <c>Program</c> graph here turns that
/// mutation red — which is exactly how the knob came to have zero readers in the first place.
/// </para>
/// </summary>
public sealed class DaemonPrPollingCoverageWiringTests
{
    [Fact]
    public void The_registered_github_provider_carries_the_configured_poll_bounds()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", "25");
            builder.UseSetting("CodeReviewDaemon:MaxPrsPerPage", "40");
        });

        var provider = configured.Services.GetServices<IPrProvider>().OfType<GitHubPrProvider>()
            .Should().ContainSingle("GitHub is always registered").Subject;

        provider.MaxPagesPerPoll.Should().Be(
            25, "the operator's CodeReviewDaemon:MaxPagesPerPoll must reach the provider Program.cs builds");
        provider.PageSize.Should().Be(40, "the operator's CodeReviewDaemon:MaxPrsPerPage must reach it too");
    }

    [Fact]
    public void The_registered_ado_provider_carries_the_configured_poll_bounds()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CodeReviewDaemon:EnableAdoProvider", "true");
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", "25");
            builder.UseSetting("CodeReviewDaemon:MaxPrsPerPage", "40");
        });

        var provider = configured.Services.GetServices<IPrProvider>().OfType<AdoPrProvider>()
            .Should().ContainSingle("EnableAdoProvider registers the ADO provider").Subject;

        provider.MaxPagesPerPoll.Should().Be(25);
        provider.PageSize.Should().Be(40);
    }

    /// <summary>
    /// The default path. With no key configured the provider must land on the documented default rather
    /// than on whatever a provider-local constant happens to say — the two agreeing today is precisely what
    /// let them disagree unnoticed.
    /// </summary>
    [Fact]
    public void An_unconfigured_host_falls_back_to_the_documented_default()
    {
        using var factory = new DaemonWebAppFactory();

        var provider = factory.Services.GetServices<IPrProvider>().OfType<GitHubPrProvider>()
            .Should().ContainSingle().Subject;

        provider.MaxPagesPerPoll.Should().Be(CodeReviewDaemonOptions.DefaultMaxPagesPerPoll);
        provider.MaxPagesPerPoll.Should().Be(10, "the documented default is 10 pages per poll");
    }

    /// <summary>
    /// A configured <c>0</c> is neither "fetch nothing" nor "no limit". Zero pages would make every repo
    /// read as permanently empty — indistinguishable from a repo with no open PRs — and unbounded is the
    /// failure the knob exists to prevent, so it degrades to the documented default in both providers.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_nonsensical_page_bound_degrades_to_the_bounded_default(string configured)
    {
        using var factory = new DaemonWebAppFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", configured));

        var provider = host.Services.GetServices<IPrProvider>().OfType<GitHubPrProvider>()
            .Should().ContainSingle().Subject;

        provider.MaxPagesPerPoll.Should().Be(
            CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
            "a value that cannot be a page count is treated as unset, never as zero pages and never as unbounded");
    }
}
