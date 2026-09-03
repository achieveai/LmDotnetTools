using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The registered <see cref="LmStreamingS2SClient"/> must carry an explicit transport budget derived from
/// <c>ReviewStageDeadlineMinutes</c>. An <see cref="HttpClient"/> built without one silently takes .NET's
/// 100-second default, and <c>SendMessageAsync</c> blocks on the review host while it builds the agent and
/// provisions its sandbox session — routinely minutes. Every review then died with
/// <c>TaskCanceledException: ... HttpClient.Timeout of 100 seconds elapsing</c> and parked the run
/// RetryPending, while the host went on to finish a review nobody collected.
/// <para>
/// Driven through the real <c>Program</c> graph, because that is the only place the defect lived: the
/// client's own unit tests supply their own <see cref="HttpClient"/> and stay green no matter what the
/// composition root builds. Deleting <c>Timeout = lmStreamingS2STimeout</c> from <c>Program.cs</c> still
/// compiles and turns exactly these tests red.
/// </para>
/// <para>
/// These pin the VALUE that reaches the transport. That the transport then enforces it — that the injected
/// client's stopwatch is the operative per-request deadline — is pinned behaviourally by
/// <c>LmStreamingS2SClientTests.The_clients_transport_timeout_is_the_deadline_for_a_stalled_review_host</c>
/// and its generous-budget twin. Neither half proves the claim alone: reading the value proves nothing about
/// enforcement, and a scaled behavioural test proves nothing about the production number.
/// </para>
/// </summary>
public sealed class DaemonS2SClientTimeoutWiringTests
{
    /// <summary>.NET's <see cref="HttpClient"/> default, i.e. what an unconfigured client silently gets.</summary>
    private static readonly TimeSpan NetDefaultTimeout = TimeSpan.FromSeconds(100);

    [Fact]
    public void The_registered_client_gets_the_configured_review_stage_budget()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:ReviewStageDeadlineMinutes", "45")
        );

        var client = configured.Services.GetRequiredService<LmStreamingS2SClient>();

        client
            .RequestTimeout.Should()
            .Be(
                TimeSpan.FromMinutes(45),
                "the operator's stage budget is the review's deadline, so a single request inside that stage "
                    + "must never be cut short by the transport's own stopwatch"
            );
    }

    /// <summary>
    /// The deployed daemon configures 60. A per-request bound equal to the WHOLE-stage budget is deliberate
    /// and is not the min-clamp #735 fixed: one request is always a strict sub-interval of the stage that
    /// contains it, so this bound can only fire for a request that has already outlived its entire stage.
    /// </summary>
    [Fact]
    public void The_registered_client_tracks_the_deployed_sixty_minute_budget()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:ReviewStageDeadlineMinutes", "60")
        );

        var client = configured.Services.GetRequiredService<LmStreamingS2SClient>();

        client.RequestTimeout.Should().Be(TimeSpan.FromMinutes(60));
    }

    /// <summary>
    /// The unconfigured daemon must be fixed too. <c>ReviewStageDeadlineMinutes</c> defaults to 30, so the
    /// default transport budget is 30 minutes — and, stated separately because it is the actual regression,
    /// never the 100 seconds an unset <see cref="HttpClient.Timeout"/> yields.
    /// </summary>
    [Fact]
    public void An_unconfigured_daemon_gets_the_option_default_and_never_the_hundred_second_default()
    {
        using var factory = new DaemonWebAppFactory();

        var client = factory.Services.GetRequiredService<LmStreamingS2SClient>();

        client.RequestTimeout.Should().Be(TimeSpan.FromMinutes(30), "ReviewStageDeadlineMinutes defaults to 30");
        client
            .RequestTimeout.Should()
            .BeGreaterThan(
                NetDefaultTimeout,
                "100 seconds is the unconfigured default that abandoned every review mid-turn"
            );
    }

    /// <summary>
    /// <c>ReviewStageDeadlineMinutes</c> is a plain non-nullable int with no validation, so <c>0</c> is
    /// reachable by configuration. <see cref="HttpClient.Timeout"/> rejects a non-positive span, which would
    /// turn a merely useless setting into a host that will not start — and the throw comes out of a DI
    /// factory, so the operator would see an ArgumentOutOfRangeException naming nothing they configured.
    /// The floor keeps the span positive; it is not a budget and does not pretend to be one.
    /// </summary>
    [Fact]
    public void A_non_positive_stage_budget_still_yields_a_startable_host()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:ReviewStageDeadlineMinutes", "0")
        );

        var resolve = () => configured.Services.GetRequiredService<LmStreamingS2SClient>();

        resolve.Should().NotThrow("a zero stage budget must not take the host down at startup");
        resolve().RequestTimeout.Should().Be(TimeSpan.FromMinutes(1));
    }
}
