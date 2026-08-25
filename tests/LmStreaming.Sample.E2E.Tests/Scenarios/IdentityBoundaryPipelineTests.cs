using System.Net;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Pins the identity boundary against the REAL host's pipeline and route table (#345, #346).
/// </summary>
/// <remarks>
/// <para>
/// Both defects were ordering defects, and ordering is a property of <c>Program.cs</c>, not of any
/// one component. A synthetic pipeline that mirrors the intended order proves the components work
/// when composed correctly; it cannot prove <c>Program.cs</c> composes them that way. Only the real
/// host can, so this suite boots it.
/// </para>
/// <para>
/// The route-partition test is here for a sharper reason. This repository has already shipped one
/// "there is a single seam" claim that a sibling controller falsified, and the identity boundary is
/// exactly that shape: a prefix guard plus a hand-maintained list of exemptions. Enumerating the
/// host's actual <see cref="EndpointDataSource"/> - which includes controllers the SDK's generated
/// <c>ApplicationPartAttribute</c> pulls in from referenced assemblies, not just the ones written in
/// this sample - is the only way to notice a route that lands outside the boundary without anyone
/// deciding it should.
/// </para>
/// </remarks>
public sealed class IdentityBoundaryPipelineTests : LoggingTestBase
{
    private const string Secret = "e2e-inbound-secret-value";
    private const string DaemonAppId = "review-daemon";
    private const string DaemonTenant = "tnt_daemon";
    private const string ClientOrigin = "https://client.example";
    private const string GuardedRoute = "/api/conversations";

    public IdentityBoundaryPipelineTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void EveryApiRouteThisHostPublishes_IsEitherGuardedOrDeliberatelyExempt()
    {
        LogTestStart();
        using var factory = NewFactory();

        var apiRoutes = ApiRoutes(factory);
        LogData("apiRoutes", apiRoutes);

        // Non-vacuity first. An empty enumeration would satisfy the partition below trivially, and
        // this test's whole value is that it fails when a NEW route appears.
        _ = apiRoutes.Should().NotBeEmpty();
        _ = apiRoutes.Should().Contain("api/conversations");

        var guarded = apiRoutes.Where(IsGuarded).ToArray();
        var exempt = apiRoutes.Where(route => !IsGuarded(route)).ToArray();

        // Both halves must be non-empty, or a guard that admitted everything - or refused
        // everything - would read as a pass.
        _ = guarded.Should().NotBeEmpty();
        _ = exempt.Should().NotBeEmpty();

        // The exempt half is the security-relevant one, so it is pinned by name. A route that joins
        // it does so by an author editing this list, never by a prefix quietly matching.
        _ = exempt.Should().BeEquivalentTo(
            [
                "api/identity/config",
                "api/admin/tenants",
                "api/admin/tenants/{tenantId}/adopt-legacy",
                "api/auth/webhook/{provider}",
                "api/auth/egress-keys",

                // The sibling this test was written to catch. It is a second route on the
                // egress-key controller, published from a referenced assembly, and the prefix in
                // InfrastructureApiPaths covers it - which is correct, and was also invisible.
                "api/auth/egress-keys/{id}",
            ],
            "every /api route outside the identity boundary is a deliberate, reviewed decision - "
                + "see IdentityMiddleware.InfrastructureApiPaths for why each one is there");
    }

    [Fact]
    public async Task WithEnforcementOn_ACorsPreflight_SurvivesTheRealPipeline()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Options, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        // A browser never attaches Authorization to a preflight, by specification. If identity runs
        // first the preflight is 401'd, no Access-Control-Allow-Origin is written, and the browser
        // abandons the real request before sending it.
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(ClientOrigin);
    }

    [Fact]
    public async Task WithEnforcementOn_ARefusalFromTheRealPipeline_IsStillReadableCrossOrigin()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // A service caller whose secret matches but whose app id is not onboarded: authenticated,
        // and refused by identity with a stable code. That is the refusal shape the SPA's rejection
        // screen is built to read.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", "not-onboarded");
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);

        var response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = response.Headers.GetValues(IdentityMiddleware.RefusalCodeHeader).Should().ContainSingle()
            .Which.Should().Be(ServiceCallerPrincipalSource.AppNotRegisteredCode);
        _ = response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(
                ClientOrigin,
                "a refusal written downstream of the CORS middleware leaves without this header, "
                    + "and a cross-origin client then sees an opaque network error instead of the "
                    + "explanation the refusal exists to give it");
    }

    [Fact]
    public async Task WithEnforcementOn_TheDaemonsHeaderShape_ReachesTheEndpointOnTheRealPipeline()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-S2S-Auth", Secret);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", DaemonAppId);
        request.Headers.TryAddWithoutValidation("X-Sbx-App-Key", "app-key-value");

        var response = await client.SendAsync(request);

        // The whole point of #345: this exact request used to be 401'd by the identity middleware
        // before the endpoint's own S2S guard ever ran.
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WithEnforcementOn_AnAnonymousBrowserRequest_IsStillRefused()
    {
        LogTestStart();
        using var factory = NewFactory(EnforcingSettings());
        using var client = factory.CreateClient();

        // The non-vacuity partner of the test above. Without this, a build that simply stopped
        // guarding /api would pass every other assertion here.
        var response = await client.GetAsync(new Uri(GuardedRoute, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = response.Headers.GetValues(IdentityMiddleware.RefusalCodeHeader).Should().ContainSingle()
            .Which.Should().Be("authentication_required");
    }

    /// <summary>
    /// Asks the real predicate rather than restating the rule, so an edit to the exemption list
    /// cannot agree with a copy of itself.
    /// </summary>
    private static bool IsGuarded(string route) =>
        IdentityMiddleware.IsGuardedApiPath(new PathString("/" + route));

    private static IReadOnlyList<string> ApiRoutes(E2EWebAppFactory factory) =>
        [..
            factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
                .Where(route => route.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(route => route, StringComparer.Ordinal),
        ];

    private static Dictionary<string, string?> EnforcingSettings() =>
        new(StringComparer.Ordinal)
        {
            ["Identity:Enforce"] = "true",
            ["Identity:DatabasePath"] = Path.Combine(
                Path.GetTempPath(),
                $"identity_e2e_{Guid.NewGuid():N}.db"),
            ["Identity:Apps:" + DaemonAppId + ":TenantId"] = DaemonTenant,
            ["Auth:S2SInboundSecret"] = Secret,
            ["LmStreaming:AllowedOrigins:0"] = ClientOrigin,
        };

    private static E2EWebAppFactory NewFactory(IDictionary<string, string?>? settings = null)
    {
        // Any scripted handler works - nothing here creates an agent.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("noop", _ => true)
            .Turn(t => t.Text("ok"))
            .Build();

        return new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()), settings);
    }
}
