using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Pins the per-credential gateway transport's own <c>HttpClient.Timeout</c> to the SAME budget the SDK
/// is configured with, instead of leaving it at .NET's 100-second default (#747).
/// </summary>
/// <remarks>
/// <para>
/// The registry derives <c>SandboxClientOptions.TransportTimeout</c> from the shared client's configured
/// <c>Timeout</c>, and the SDK enforces that value with a <see cref="CancellationToken"/> linked into
/// every one of its send sites — deliberately NOT through <c>HttpClient.Timeout</c>, because mutating a
/// borrowed client's timeout would hit every other caller of it. The per-credential transport's own
/// timeout is therefore a BACKSTOP that should never be the deadline that fires.
/// </para>
/// <para>
/// Left defaulted it is a second, uncoupled deadline. It cannot be reached under either shipped
/// configuration — both hosts pass a 30-second shared client, so 30 s bounds every call twice over
/// before 100 s is approached — but a deployment that configures a budget ABOVE 100 s gets it silently
/// capped, and the cap surfaces as a bare <see cref="TaskCanceledException"/> rather than the SDK's
/// <c>SandboxException(TransportTimeout)</c>, which is the exception carrying the operationId a caller
/// needs to re-poll a command that has already run. These tests assert the two numbers cannot disagree.
/// </para>
/// <para>
/// Asserted as a property of the constructed transport rather than end-to-end, and deliberately: making
/// the defaulted 100 s deadline actually fire requires a stall longer than 100 s of real wall clock,
/// which no CI-appropriate test can pay for. <c>SandboxClient.TransportClock</c> can fast-forward the
/// SDK's budget; nothing can fast-forward <c>HttpClient.Timeout</c>.
/// </para>
/// </remarks>
public class SandboxSessionRegistryTransportBudgetTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    /// <summary>
    /// .NET's default <c>HttpClient.Timeout</c> — the value the transport silently took before #747, and
    /// the one number a correct derivation can never coincidentally produce here.
    /// </summary>
    private static readonly TimeSpan DotNetDefaultTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// A configured budget deliberately ABOVE the 100 s default: below it, a transport still stuck on the
    /// default would look indistinguishable from a correctly derived one that happened to be larger.
    /// </summary>
    private static readonly TimeSpan ConfiguredBudget = TimeSpan.FromSeconds(240);

    /// <summary>
    /// The whole defect in one assertion: with a shared client configured well above the default, the
    /// per-credential transport must not still be sitting on 100 s.
    /// </summary>
    [Fact]
    public async Task PerCredentialTransport_DoesNotInheritTheDotNetDefaultTimeout()
    {
        await using var registry = CreateRegistry(ConfiguredBudget);

        _ = await registry.GetOrCreateSessionAsync("ws", CancellationToken.None, new SandboxCredential("app", ""));

        registry
            .PerCredentialTransportTimeouts.Should()
            .ContainSingle()
            .Which.Should()
            .NotBe(DotNetDefaultTimeout, "a defaulted transport silently caps any budget configured above it");
    }

    /// <summary>
    /// The direction of the fix, which is the half easy to get backwards: the backstop must sit STRICTLY
    /// ABOVE the SDK's budget. Equal to it would be a race between two deadlines armed at the same
    /// instant, and the loser decides whether the caller sees the SDK's typed timeout or a bare
    /// <see cref="TaskCanceledException"/>; below it would be the same defect with a different number.
    /// </summary>
    [Fact]
    public async Task PerCredentialTransport_TimeoutSitsStrictlyAboveTheConfiguredBudget()
    {
        await using var registry = CreateRegistry(ConfiguredBudget);

        _ = await registry.GetOrCreateSessionAsync("ws", CancellationToken.None, new SandboxCredential("app", ""));

        registry.PerCredentialTransportTimeouts.Should().ContainSingle().Which.Should().BeGreaterThan(ConfiguredBudget);
    }

    /// <summary>
    /// The other half of the direction, and the reason #747 warned against reaching for
    /// <c>Timeout.InfiniteTimeSpan</c>: an unbounded backstop is only safe while EVERY SDK send arms the
    /// linked budget, which is a property of the SDK this class does not own. A send that ever escapes
    /// that budget must still surface rather than hang.
    /// <para>
    /// Green before the change as well as after — the default is finite too. It is a regression guard on
    /// the direction, not evidence of the defect, and is reported as such.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PerCredentialTransport_TimeoutStaysBounded()
    {
        await using var registry = CreateRegistry(ConfiguredBudget);

        _ = await registry.GetOrCreateSessionAsync("ws", CancellationToken.None, new SandboxCredential("app", ""));

        registry.PerCredentialTransportTimeouts.Should().ContainSingle().Which.Should().NotBe(Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The shared client's timeout is what the registry derives the SDK budget from, so the backstop must
    /// track it rather than being a constant that merely happens to clear one configuration. A second
    /// budget, an order of magnitude apart, moves the transport with it.
    /// </summary>
    [Fact]
    public async Task PerCredentialTransport_TracksTheSharedClientsConfiguredTimeout()
    {
        var small = TimeSpan.FromSeconds(30);
        await using var tight = CreateRegistry(small);
        await using var loose = CreateRegistry(ConfiguredBudget);

        _ = await tight.GetOrCreateSessionAsync("ws", CancellationToken.None, new SandboxCredential("app", ""));
        _ = await loose.GetOrCreateSessionAsync("ws", CancellationToken.None, new SandboxCredential("app", ""));

        var tightTimeout = tight.PerCredentialTransportTimeouts.Should().ContainSingle().Subject;
        var looseTimeout = loose.PerCredentialTransportTimeouts.Should().ContainSingle().Subject;

        looseTimeout.Should().BeGreaterThan(tightTimeout);
        (looseTimeout - tightTimeout)
            .Should()
            .Be(ConfiguredBudget - small, "the backstop is the budget plus a fixed margin, so it moves 1:1 with it");
    }

    private static SandboxSessionRegistry CreateRegistry(TimeSpan sharedClientTimeout)
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubGateway())
        );

        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            // The shared borrowed transport. Its Timeout is the ONE configured number the registry reads:
            // it becomes the SDK's TransportTimeout, and the per-credential backstop must follow it.
            new HttpClient(new StubGateway()) { Timeout = sharedClientTimeout },
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-transport-budget", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    /// <summary>Answers a create with a well-formed session and every other call with a bare 200.</summary>
    private sealed class StubGateway : HttpMessageHandler
    {
        private int _creates;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (
                request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("/sandboxes", StringComparison.Ordinal)
            )
            {
                var n = Interlocked.Increment(ref _creates);
                var body =
                    $"{{\"session_id\":\"sess-{n}\",\"container_id\":\"c-{n}\",\"volumes\":{{\"workspace\":"
                    + "{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":7}}}";
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    }
                );
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
