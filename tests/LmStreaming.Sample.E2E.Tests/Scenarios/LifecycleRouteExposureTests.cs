using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// The lifecycle control plane (#227) is off by default, and this asserts what "off" means for the
/// sample's HTTP surface: the routes are <em>absent</em>, not present-and-refusing.
/// </summary>
/// <remarks>
/// <para>
/// This has to run against the real host rather than against a synthetic one, because the thing it
/// guards is a build-time side effect nothing in the source states. The .NET SDK writes an
/// <c>ApplicationPartAttribute</c> into this sample for every referenced assembly that references
/// MVC — <c>LmAgentInfra</c> among them — so MVC discovers that assembly's controllers here whether
/// or not the sample asked for them. Before <c>AddLifecycleControlPlane</c> was called in
/// <c>Program.cs</c>, the two lifecycle controllers were therefore published on a default
/// configuration with none of their dependencies registered.
/// </para>
/// <para>
/// The <c>api/auth</c> assertions are the other half, and the reason the fix is not simply an
/// allow-list. Those routes come from the same assembly and this sample has been serving them all
/// along; a narrowing that removed them would be a regression introduced by turning on an unrelated
/// feature. Which of the two answers applies is decided by whether the host supplied the application
/// part, so both directions are pinned here.
/// </para>
/// </remarks>
public sealed class LifecycleRouteExposureTests : LoggingTestBase
{
    private const string SubscriptionsRoute = "api/lifecycle/subscriptions";
    private const string DecisionsRoute = "api/lifecycle/approvals/decisions";
    private const string WebhookRoute = "api/auth/webhook/{provider}";
    private const string EgressKeysRoute = "api/auth/egress-keys";

    public LifecycleRouteExposureTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public void A_default_configuration_publishes_no_lifecycle_control_plane()
    {
        LogTestStart();
        using var factory = NewFactory();

        var routes = Routes(factory);

        routes.Should().NotContain(route => route.StartsWith("api/lifecycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Turning_lifecycle_off_does_not_take_away_the_sample_existing_endpoints()
    {
        LogTestStart();
        using var factory = NewFactory();

        var routes = Routes(factory);

        // Both live in LmAgentInfra beside the lifecycle controllers. They were reachable here before
        // #227 and must still be — that they share an assembly with a feature this sample does not
        // enable is not a reason to unpublish them.
        routes.Should().Contain(WebhookRoute);
        routes.Should().Contain(EgressKeysRoute);
    }

    [Fact]
    public void Enabling_delivery_publishes_the_subscription_routes()
    {
        LogTestStart();
        using var factory = NewFactory(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Lifecycle:Delivery:Enabled"] = "true",
                // The allow-list defaults to empty and admits nothing. Registration is not exercised
                // here, but the options are validated at wiring time, so a host that enables delivery
                // without one does not boot.
                ["Lifecycle:Delivery:AllowedCallbackHosts:0"] = "callbacks.example.com",
            }
        );

        var routes = Routes(factory);
        LogData("routes", routes);

        routes.Should().Contain(SubscriptionsRoute);

        // Approval is a separate flag. Observing runs must not start gating them.
        routes.Should().NotContain(DecisionsRoute);
        routes.Should().Contain(WebhookRoute);
    }

    [Fact]
    public void Enabling_approval_publishes_the_decision_route()
    {
        LogTestStart();
        using var factory = NewFactory(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Lifecycle:Delivery:Enabled"] = "true",
                ["Lifecycle:Delivery:AllowedCallbackHosts:0"] = "callbacks.example.com",
                ["Lifecycle:Approval:Enabled"] = "true",
            }
        );

        var routes = Routes(factory);

        routes.Should().Contain(DecisionsRoute);
        routes.Should().Contain(SubscriptionsRoute);
        routes.Should().Contain(WebhookRoute);
    }

    private static IReadOnlyList<string> Routes(E2EWebAppFactory factory) =>
        [
            .. factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty),
        ];

    private E2EWebAppFactory NewFactory(IDictionary<string, string?>? settings = null)
    {
        // Any scripted handler works — nothing here creates an agent; only the route table is read.
        var responder = ScriptedSseResponder.New().ForRole("noop", _ => true).Turn(t => t.Text("ok")).Build();

        return new E2EWebAppFactory("test", new ScriptedBuilder(responder.AsAnthropicHandler()), settings);
    }
}
