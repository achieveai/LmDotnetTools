using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — host wiring. The claims under test are mostly negative, and they are the ones a unit
/// test of any single component cannot make: with the flags off nothing is registered and no route
/// exists; with them on, the routes that appear are the lifecycle ones and <em>only</em> those, even
/// though this assembly also carries <c>api/auth/webhook</c> and <c>api/auth/egress-keys</c>.
/// </summary>
/// <remarks>
/// <para>
/// The controllers' own behaviour is covered by <see cref="LifecycleSubscriptionsControllerTests"/>
/// and <see cref="LifecycleApprovalControllerTests"/>, which drive them directly. What those cannot
/// show is which of them MVC actually publishes, and on a default configuration the answer is "both,
/// whether or not the host enabled anything" — so an endpoint can be fully implemented, fully
/// tested, and reachable in a host that never asked for it. That gap is what this file closes.
/// </para>
/// <para>
/// The <c>api/auth</c> endpoints turn on a second axis, which is why they are asserted both ways
/// here. The .NET SDK emits an <see cref="ApplicationPartAttribute"/> for every referenced assembly
/// that references MVC, so a host can be publishing this assembly's controllers without ever having
/// said so — and on such a host taking those endpoints away is a regression, while on a host that
/// only asked for a lifecycle control plane, adding them is an exposure. Who supplied the part is
/// the only signal that separates the two.
/// </para>
/// </remarks>
public sealed class LifecycleHostingExtensionsTests
{
    private const string CallbackHost = "callbacks.example.com";

    /// <summary>The route of a controller that lives in this assembly, standing in for the host's own.</summary>
    private const string HostProbeRoute = "api/host/probe";

    private static readonly Assembly LifecycleAssembly = typeof(LifecycleSubscriptionsController).Assembly;

    [Fact]
    public void Nothing_is_registered_until_a_flag_is_set()
    {
        using var provider = BuildServices(Configuration());

        provider.GetService<ILifecyclePublisher>().Should().BeNull();
        provider.GetService<LifecycleDeliveryPipeline>().Should().BeNull();
        provider.GetService<MultiTurnLifecycleServices>().Should().BeNull();
        provider.GetService<RemoteApprovalStore>().Should().BeNull();
        provider.GetServices<IToolApprovalGate>().Should().BeEmpty();
        provider.GetServices<IHostedService>().Should().BeEmpty();
    }

    [Fact]
    public void The_pipeline_is_one_object_behind_all_three_of_its_registrations()
    {
        using var provider = BuildServices(Configuration(delivery: true));

        var pipeline = provider.GetRequiredService<LifecycleDeliveryPipeline>();

        // Not an aesthetic point. The hosted-service registration is what gives shutdown a live queue
        // to drain; a second instance would drain an empty one while the queue that mattered was
        // abandoned, and every event still in flight would be lost with nothing reporting it.
        provider.GetRequiredService<ILifecyclePublisher>().Should().BeSameAs(pipeline);
        provider.GetServices<IHostedService>().Should().ContainSingle().Which.Should().BeSameAs(pipeline);
    }

    [Fact]
    public void Every_egress_check_reads_one_options_instance()
    {
        using var provider = BuildServices(Configuration(delivery: true));

        var options = provider.GetRequiredService<LifecycleDeliveryOptions>();

        options.Enabled.Should().BeTrue();
        options.AllowedCallbackHosts.Should().Equal(CallbackHost);

        // Registration, enqueue, and every delivery attempt each re-check the destination. Their
        // agreement is only guaranteed if all three are reading the same object; a second binding
        // could hold a different allow-list and admit at one moment what another refuses.
        provider.GetRequiredService<LifecycleDeliveryOptions>().Should().BeSameAs(options);
    }

    [Fact]
    public void The_delivery_client_is_built_to_refuse_redirects()
    {
        using var provider = BuildServices(Configuration(delivery: true));

        var options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(LifecycleHostingExtensions.DeliveryHttpClientName);

        using var probe = new HandlerBuilderProbe { Name = LifecycleHostingExtensions.DeliveryHttpClientName };
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(probe);
        }

        // A 302 from an allow-listed callback would otherwise re-POST the signed body — conversation
        // content, or a tool's arguments — to whatever host the response named. The sender reports a
        // 3xx as a permanent rejection, and that report is only honest if nothing chased it first.
        probe.PrimaryHandler.Should().BeOfType<SocketsHttpHandler>().Which.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public void Ownership_resolution_defaults_to_the_sandbox_registry()
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddLifecycleDelivery(Configuration(delivery: true));

        _ = services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(ILifecycleOwnerResolver));

        // Resolved rather than read off the descriptor, and that is the point of the assertion as
        // much as the type is: building it must not reach for a SandboxSessionRegistry, which is not
        // registered here and which — on a host where it is — depends on this object's own dependent.
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILifecycleOwnerResolver>().Should().BeOfType<SandboxLifecycleOwnerResolver>();
    }

    [Fact]
    public async Task Ownership_resolution_asks_the_host_for_its_registry_when_an_event_arrives()
    {
        // The other half of the test above. Deferral is only safe if the deferred lookup actually
        // happens; a resolver that never asked would answer "no owner" for everything and quietly
        // turn delivery off.
        var asked = 0;
        var resolver = new SandboxLifecycleOwnerResolver(() =>
        {
            asked++;
            throw new InvalidOperationException("registry requested");
        });

        asked.Should().Be(0);

        var act = async () => await resolver.ResolveThreadOwnerAsync("thread-1");

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        asked.Should().Be(1);
    }

    [Fact]
    public void A_host_that_scopes_ownership_its_own_way_keeps_its_resolver()
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
        _ = services.AddLifecycleDelivery(Configuration(delivery: true));

        services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(ILifecycleOwnerResolver))
            .Which.ImplementationInstance.Should()
            .BeOfType<NoOwnerResolver>();
    }

    [Fact]
    public void Approval_without_delivery_is_refused_rather_than_treated_as_stricter()
    {
        var services = new ServiceCollection();

        var act = () => _ = services.AddRemoteToolApproval(Configuration(approval: true));

        // Silently allowing it would produce a host where every gated tool call blocks until it
        // expires, because the gate would have no registry to find an approver in and no transport to
        // reach one — a total outage that reads, from inside, like nobody answering.
        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(RemoteApprovalOptions.SectionName);
        message.Should().Contain(LifecycleDeliveryOptions.SectionName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Approval_wiring_does_not_depend_on_the_order_the_host_called_these_in(bool approvalFirst)
    {
        using var provider = BuildServices(Configuration(delivery: true, approval: true), approvalFirst: approvalFirst);

        var bundle = provider.GetRequiredService<MultiTurnLifecycleServices>();

        bundle.Publisher.Should().BeSameAs(provider.GetRequiredService<LifecycleDeliveryPipeline>());
        bundle.Approval.Should().NotBeSameAs(ToolInvocationPreparer.Disabled);
        provider
            .GetServices<IToolApprovalGate>()
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<RemoteToolApprovalGate>();
    }

    [Fact]
    public void Delivery_alone_leaves_every_tool_call_ungated()
    {
        using var provider = BuildServices(Configuration(delivery: true));

        var bundle = provider.GetRequiredService<MultiTurnLifecycleServices>();

        // Observing a run and gating it are separate opt-ins. Turning on delivery must not quietly
        // start blocking tool calls on a decision nobody has been asked for.
        bundle.Approval.Should().BeSameAs(ToolInvocationPreparer.Disabled);
        bundle.Publisher.Should().BeSameAs(provider.GetRequiredService<LifecycleDeliveryPipeline>());
        bundle.TimeProvider.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void A_host_gate_is_joined_rather_than_replaced()
    {
        using var provider = BuildServices(
            Configuration(delivery: true, approval: true),
            before: services => services.AddSingleton<IToolApprovalGate, AlwaysAllowGate>()
        );

        // Every gate in the list must allow a call, so joining is the strict direction and replacing
        // would silently drop whatever policy the host had already installed.
        provider
            .GetServices<IToolApprovalGate>()
            .Select(gate => gate.GetType())
            .Should()
            .BeEquivalentTo([typeof(AlwaysAllowGate), typeof(RemoteToolApprovalGate)]);
    }

    [Fact]
    public async Task Delivery_publishes_the_subscription_routes_and_nothing_else_from_this_assembly()
    {
        var configuration = Configuration(delivery: true);
        var builder = CreateHostBuilder();
        _ = WithLifecycleServices(builder, configuration).AddControllers().AddLifecycleControlPlane(configuration);

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        templates.Should().Contain("api/lifecycle/subscriptions");
        templates.Should().Contain("api/lifecycle/subscriptions/{subscriptionId}");

        // This host had no part for this assembly until the line above added one, so the assembly is
        // here on lifecycle's account alone and nothing else in it is published. The opposite case —
        // a host the SDK already wired up — is asserted separately.
        templates.Should().NotContain(t => t.StartsWith("api/auth/", StringComparison.Ordinal));

        // Approval is off, so its route is absent rather than present-and-refusing.
        templates.Should().NotContain(t => t.StartsWith("api/lifecycle/approvals", StringComparison.Ordinal));

        // ...and the host's own controllers are untouched. Narrowing discovery meant replacing MVC's
        // default provider, which makes this method responsible for everyone else's controllers too.
        templates.Should().Contain(HostProbeRoute);
    }

    [Fact]
    public async Task Enabling_approval_adds_its_route_and_only_its_route()
    {
        var configuration = Configuration(delivery: true, approval: true);
        var builder = CreateHostBuilder();
        _ = WithLifecycleServices(builder, configuration).AddControllers().AddLifecycleControlPlane(configuration);

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        templates.Should().Contain("api/lifecycle/approvals/decisions");
        templates.Should().Contain("api/lifecycle/subscriptions");
        templates.Should().Contain(HostProbeRoute);
        templates.Should().NotContain(t => t.StartsWith("api/auth/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_both_flags_off_the_assembly_is_not_even_an_application_part()
    {
        var builder = CreateHostBuilder();
        var mvc = builder.Services.AddControllers();

        _ = mvc.AddLifecycleControlPlane(Configuration());

        ApplicationPartManager? parts = null;
        _ = mvc.ConfigureApplicationPartManager(manager => parts = manager);

        await using var app = builder.Build();

        // Absent, not refusing. A route that answers 403 tells a prober the feature exists here; a
        // route that was never registered tells them nothing.
        RouteTemplates(app.Services)
            .Should()
            .NotContain(t => t.StartsWith("api/lifecycle", StringComparison.Ordinal))
            .And.Contain(HostProbeRoute);

        parts!.ApplicationParts.OfType<AssemblyPart>().Should().NotContain(part => part.Assembly == LifecycleAssembly);
    }

    [Fact]
    public async Task A_host_the_sdk_already_wired_up_keeps_its_other_endpoints()
    {
        var configuration = Configuration(delivery: true);
        var builder = CreateHostBuilder();
        var mvc = WithLifecycleServices(builder, configuration).AddControllers();
        _ = mvc.ConfigureApplicationPartManager(parts =>
            parts.ApplicationParts.Add(new AssemblyPart(LifecycleAssembly))
        );

        _ = mvc.AddLifecycleControlPlane(configuration);

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        templates.Should().Contain("api/lifecycle/subscriptions");

        // This host was already serving the authentication endpoints before it had ever heard of
        // lifecycle delivery — the SDK writes an ApplicationPartAttribute for every referenced
        // assembly that references MVC, so it did not choose them either. Turning on delivery must
        // not take them away; both samples in this repository are exactly this case.
        templates.Should().Contain("api/auth/egress-keys");
        templates.Should().Contain("api/auth/webhook/{provider}");

        templates.Should().NotContain(t => t.StartsWith("api/lifecycle/approvals", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_the_sdk_already_wired_up_still_loses_the_lifecycle_routes_when_disabled()
    {
        var builder = CreateHostBuilder();
        var mvc = builder.Services.AddControllers();
        _ = mvc.ConfigureApplicationPartManager(parts =>
            parts.ApplicationParts.Add(new AssemblyPart(LifecycleAssembly))
        );

        _ = mvc.AddLifecycleControlPlane(Configuration());

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        // The case this method exists for. Without it these routes are published on any host that
        // merely references this assembly, and they cannot even be constructed — their options are
        // registered only when the flags are on — so an unauthenticated POST reaches a 500 rather
        // than a 404. Calling it with everything off is what turns published-and-broken into absent.
        templates.Should().NotContain(t => t.StartsWith("api/lifecycle", StringComparison.Ordinal));
        templates.Should().Contain("api/auth/egress-keys");
        templates.Should().Contain(HostProbeRoute);
    }

    [Fact]
    public void A_host_with_its_own_controller_feature_provider_is_refused()
    {
        var configuration = Configuration(delivery: true);
        var builder = CreateHostBuilder();
        var mvc = WithLifecycleServices(builder, configuration).AddControllers();
        _ = mvc.ConfigureApplicationPartManager(parts =>
            parts.FeatureProviders.Add(new HostControllerFeatureProvider())
        );

        var act = () => _ = mvc.AddLifecycleControlPlane(configuration);

        // MVC unions what its feature providers return, so an allow-list beside another allow-list is
        // not an allow-list. Refusing is the only answer that does not silently widen one of them.
        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(nameof(HostControllerFeatureProvider));
        message.Should().Contain(nameof(LifecycleSubscriptionsController));
    }

    [Fact]
    public void A_host_provider_that_is_not_a_ControllerFeatureProvider_is_refused_just_the_same()
    {
        // MVC resolves feature providers by interface, not by base class: anything implementing
        // IApplicationFeatureProvider<ControllerFeature> is consulted and unioned in. A scan that
        // looked only for ControllerFeatureProvider subclasses would walk straight past this one,
        // leave it installed, and hand back a "narrowed" discovery that is the union of two lists.
        var configuration = Configuration(delivery: true);
        var builder = CreateHostBuilder();
        var mvc = WithLifecycleServices(builder, configuration).AddControllers();
        _ = mvc.ConfigureApplicationPartManager(parts =>
            parts.FeatureProviders.Add(new HostInterfaceFeatureProvider())
        );

        var act = () => _ = mvc.AddLifecycleControlPlane(configuration);

        act.Should()
            .Throw<InvalidOperationException>()
            .Which.Message.Should()
            .Contain(nameof(HostInterfaceFeatureProvider));
    }

    [Fact]
    public async Task Calling_this_twice_leaves_the_same_host_as_calling_it_once()
    {
        // Composition roots get called twice — a shared extension method that wires the control plane,
        // invoked by both a host and a library it uses. The second call must not read the first call's
        // own provider as a host provider it may not overrule, and must not add a second part for this
        // assembly, which would discover every controller in it twice and make each route ambiguous.
        var configuration = Configuration(delivery: true, approval: true);
        var builder = CreateHostBuilder();
        var mvc = WithLifecycleServices(builder, configuration).AddControllers();

        _ = mvc.AddLifecycleControlPlane(configuration);
        var again = () => _ = mvc.AddLifecycleControlPlane(configuration);
        again.Should().NotThrow();

        ApplicationPartManager? parts = null;
        _ = mvc.ConfigureApplicationPartManager(manager => parts = manager);

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        templates.Should().Contain("api/lifecycle/subscriptions");
        templates.Should().Contain("api/lifecycle/approvals/decisions");
        templates.Should().Contain(HostProbeRoute);
        templates.Should().NotContain(t => t.StartsWith("api/auth/", StringComparison.Ordinal));
        templates.Should().OnlyHaveUniqueItems();

        parts!
            .ApplicationParts.OfType<AssemblyPart>()
            .Should()
            .ContainSingle(part => part.Assembly == LifecycleAssembly);
    }

    [Fact]
    public async Task A_part_the_host_adds_after_this_method_still_brings_its_endpoints()
    {
        // ConfigureApplicationPartManager runs its callback immediately, so this method sees the part
        // list mid-composition. Deciding then whether the host supplied this assembly would answer
        // "no" for every host that wires MVC in the order below — and take away the authentication
        // endpoints it went on to ask for. The question is therefore asked at discovery time instead.
        var configuration = Configuration(delivery: true);
        var builder = CreateHostBuilder();
        var mvc = WithLifecycleServices(builder, configuration).AddControllers();

        _ = mvc.AddLifecycleControlPlane(configuration);
        _ = mvc.ConfigureApplicationPartManager(parts =>
            parts.ApplicationParts.Add(new AssemblyPart(LifecycleAssembly))
        );

        await using var app = builder.Build();
        var templates = RouteTemplates(app.Services);

        templates.Should().Contain("api/lifecycle/subscriptions");
        templates.Should().Contain("api/auth/egress-keys");
        templates.Should().Contain("api/auth/webhook/{provider}");
    }

    [Theory]
    [InlineData(true, false, nameof(LifecycleHostingExtensions.AddLifecycleDelivery))]
    [InlineData(true, true, nameof(LifecycleHostingExtensions.AddRemoteToolApproval))]
    public void A_route_is_not_published_when_the_host_never_wired_what_serves_it(
        bool delivery,
        bool approval,
        string missing
    )
    {
        // The flag says the host wants the feature; the container says whether it actually wired it.
        // When they disagree the route would be published and its controller unconstructible — a 500
        // that tells a prober the feature is here and misconfigured, which is precisely the outcome
        // this method exists to prevent, reached from the other direction. So the disagreement is
        // reported to the host at startup instead, naming the call that is missing and its order.
        var configuration = Configuration(delivery, approval);
        var builder = CreateHostBuilder();
        if (approval)
        {
            // Delivery is wired in the approval case, so the only thing missing is the approval half.
            _ = builder.Services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
            _ = builder.Services.AddLifecycleDelivery(configuration);
        }

        var act = () => _ = builder.Services.AddControllers().AddLifecycleControlPlane(configuration);

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(missing);
        message.Should().Contain(nameof(LifecycleHostingExtensions.AddLifecycleControlPlane));
    }

    [Fact]
    public async Task The_control_plane_answers_on_a_real_host_while_the_auth_endpoints_stay_absent()
    {
        var configuration = Configuration(
            delivery: true,
            approval: true,
            // Nothing is queued, so there is nothing to drain; zero keeps the assertion about routing
            // rather than about how long this host is willing to wait on shutdown.
            extra: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Lifecycle:Delivery:ShutdownDrainTimeout"] = "00:00:00",
            }
        );

        var builder = CreateHostBuilder();
        _ = builder.Services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
        _ = builder.Services.AddLifecycleDelivery(configuration);
        _ = builder.Services.AddRemoteToolApproval(configuration);
        _ = builder.Services.AddControllers().AddLifecycleControlPlane(configuration);

        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        _ = app.MapControllers();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // Unauthenticated, so a refusal is the expected answer — and a refusal is the proof a route
        // table cannot give: the endpoint matched *and* every dependency the controller needs
        // resolved out of the real container.
        using var registration = await client.PostAsync(
            "api/lifecycle/subscriptions",
            JsonContent.Create(new { callback_uri = $"https://{CallbackHost}/hook" })
        );
        registration.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Asserted as "not absent" rather than as a status: what varies with the body shape is which
        // refusal comes back, and the claim here is only that MVC routed to the controller at all.
        using var decision = await client.PostAsync(
            "api/lifecycle/approvals/decisions",
            JsonContent.Create(new { request_id = "r-1" })
        );
        decision.StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        using var egressKeys = await client.GetAsync("api/auth/egress-keys");
        egressKeys.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var authWebhook = await client.PostAsync("api/auth/webhook/github", JsonContent.Create(new { }));
        authWebhook.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await app.StopAsync();
    }

    [Fact]
    public async Task The_subscription_endpoint_is_constructible_on_a_host_that_enabled_only_delivery()
    {
        // Delivery and approval are independent opt-ins, and RemoteApprovalStore is registered by only
        // one of them — so the subscriptions controller takes it as an optional constructor parameter.
        // MVC builds controllers through ActivatorUtilities, which honours a defaulted parameter; if it
        // did not, this host would answer 500 on every subscription request rather than 403, and no
        // route-table assertion would notice. That is the whole reason this boots a real container.
        var configuration = Configuration(
            delivery: true,
            extra: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Lifecycle:Delivery:ShutdownDrainTimeout"] = "00:00:00",
            }
        );

        var builder = CreateHostBuilder();
        _ = builder.Services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
        _ = builder.Services.AddLifecycleDelivery(configuration);
        _ = builder.Services.AddControllers().AddLifecycleControlPlane(configuration);

        await using var app = builder.Build();
        app.Services.GetService<RemoteApprovalStore>().Should().BeNull("approval was never enabled");
        app.Urls.Add("http://127.0.0.1:0");
        _ = app.MapControllers();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var registration = await client.PostAsync(
            "api/lifecycle/subscriptions",
            JsonContent.Create(new { callback_uri = $"https://{CallbackHost}/hook" })
        );
        registration.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var removal = await client.DeleteAsync("api/lifecycle/subscriptions/sub-a");
        removal.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await app.StopAsync();
    }

    [Fact]
    public async Task An_allow_listed_name_is_still_refused_when_the_address_behind_it_is_private()
    {
        // The rebinding case, end to end. `localhost` is allow-listed, so every name-level check
        // passes and the only thing left to refuse the delivery is the address the name resolves to
        // at the moment of connection — which is what this asserts, on a real socket, through the
        // delivery client the host actually wires up.
        //
        // The subscriber has to be live. "The delivery never arrived" says nothing if nothing was
        // listening, so the same subscriber is dialled twice: once under a configuration that refuses
        // private space and once under one that permits it. The second half is the control — it is
        // what makes the first half a statement about the address rule rather than about a callback
        // URL that was never going to work.
        var hits = 0;
        var builder = CreateHostBuilder();
        await using var subscriber = builder.Build();
        _ = subscriber.MapPost(
            "/hook",
            () =>
            {
                _ = Interlocked.Increment(ref hits);
                return Results.NoContent();
            }
        );
        subscriber.Urls.Add("http://127.0.0.1:0");
        await subscriber.StartAsync();

        var callback = new Uri($"http://localhost:{new Uri(subscriber.Urls.First()).Port}/hook");

        await using (var closed = BuildServices(DeliveryToLoopback(allowPrivate: false)))
        {
            var refused = await SendOneAsync(closed, callback);

            // Retryable, not permanent: from the sender's side this is a connection that did not
            // open, and a name that resolves into private space today may legitimately resolve
            // elsewhere tomorrow. The reason token stays opaque — an operator learns the destination
            // was unreachable, not what it resolved to.
            refused.Outcome.Should().Be(LifecycleDeliveryOutcome.Retryable);
            refused.Reason.Should().Be("transport");
            refused.StatusCode.Should().BeNull("nothing answered, so there is no status to report");
            Volatile.Read(ref hits).Should().Be(0, "the connection was refused before any bytes left");
        }

        await using (var open = BuildServices(DeliveryToLoopback(allowPrivate: true)))
        {
            var delivered = await SendOneAsync(open, callback);

            delivered
                .Outcome.Should()
                .Be(
                    LifecycleDeliveryOutcome.Succeeded,
                    "the same URL, the same subscriber, and the same client — only the address rule "
                        + "differs, so this is what the refusal above was caused by"
                );
            Volatile.Read(ref hits).Should().Be(1);
        }

        await subscriber.StopAsync();
    }

    [Fact]
    public async Task A_subscriber_cannot_redirect_a_signed_delivery_to_a_host_that_was_never_admitted()
    {
        // The other half of the same rule, and the half validating addresses cannot cover: a redirect
        // target is named by the far side *after* every check has passed. Chasing it would re-POST the
        // signed body — conversation content, or a tool's arguments — to a host the allow-list never
        // admitted and the connect callback never saw.
        //
        // Private space is deliberately open here and the redirect points at the same live server, so
        // nothing but the handler's own refusal can keep the second endpoint untouched. A test that
        // pointed somewhere unreachable would pass against a client that follows redirects.
        var hooked = 0;
        var followed = 0;
        string? elsewhere = null;

        var builder = CreateHostBuilder();
        await using var subscriber = builder.Build();
        _ = subscriber.MapPost(
            "/hook",
            () =>
            {
                _ = Interlocked.Increment(ref hooked);

                // 307 rather than 302: it is the one that tells a client to repeat the POST verbatim,
                // body and all, which is precisely the behaviour under test.
                return Results.Redirect(elsewhere!, permanent: false, preserveMethod: true);
            }
        );
        _ = subscriber.MapPost(
            "/elsewhere",
            () =>
            {
                _ = Interlocked.Increment(ref followed);
                return Results.NoContent();
            }
        );
        subscriber.Urls.Add("http://127.0.0.1:0");
        await subscriber.StartAsync();

        // The port is not known until the server is listening, so the redirect target is filled in
        // here rather than at MapPost.
        var origin = subscriber.Urls.First();
        elsewhere = $"{origin}/elsewhere";

        await using var services = BuildServices(DeliveryToLoopback(allowPrivate: true));
        var result = await SendOneAsync(services, new Uri($"http://localhost:{new Uri(origin).Port}/hook"));

        Volatile.Read(ref hooked).Should().Be(1, "the delivery reached the endpoint it was sent to");
        Volatile.Read(ref followed).Should().Be(0, "and went no further");

        // Reported as the subscriber rejecting the request, which is what an unchased redirect is, and
        // as permanent because repeating it would only earn the same redirect again.
        result.Outcome.Should().Be(LifecycleDeliveryOutcome.Permanent);
        result.StatusCode.Should().Be(StatusCodes.Status307TemporaryRedirect);

        await subscriber.StopAsync();
    }

    /// <summary>
    /// Delivery configured to dial a subscriber on this machine: the loopback name is allow-listed, so
    /// only <see cref="LifecycleDeliveryOptions.AllowPrivateCallbackAddresses"/> decides the outcome.
    /// </summary>
    private static IConfiguration DeliveryToLoopback(bool allowPrivate) =>
        Configuration(
            delivery: true,
            extra: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Lifecycle:Delivery:AllowedCallbackHosts:0"] = "localhost",
                // Otherwise registration refuses the plaintext callback for an unrelated reason and
                // the connect rule is never reached.
                ["Lifecycle:Delivery:RequireHttpsCallbacks"] = "false",
                ["Lifecycle:Delivery:AllowPrivateCallbackAddresses"] = allowPrivate ? "true" : "false",
            }
        );

    /// <summary>
    /// Registers a subscriber through the real registry and makes one delivery attempt to it through
    /// the container's own sender — the sender, not the pipeline, so the only egress check in play is
    /// the one performed at connect time.
    /// </summary>
    private static async Task<LifecycleDeliveryResult> SendOneAsync(IServiceProvider services, Uri callback)
    {
        var grant = services
            .GetRequiredService<ILifecycleSubscriptionRegistry>()
            .Register(
                LifecycleOwnerKey.ForAppId("app-a"),
                "app-a",
                new LifecycleSubscriptionRequest { CallbackUri = callback }
            );

        return await services
            .GetRequiredService<ILifecycleDeliverySender>()
            .SendAsync(grant.Subscription, "delivery-1", "{}"u8.ToArray(), CancellationToken.None);
    }

    private static IConfiguration Configuration(
        bool delivery = false,
        bool approval = false,
        IDictionary<string, string?>? extra = null
    )
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (delivery)
        {
            values["Lifecycle:Delivery:Enabled"] = "true";

            // The default allow-list is empty and admits nothing. Left there, a registration test
            // would be refused for a reason unrelated to the wiring it is about.
            values["Lifecycle:Delivery:AllowedCallbackHosts:0"] = CallbackHost;
        }

        if (approval)
        {
            values["Lifecycle:Approval:Enabled"] = "true";
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildServices(
        IConfiguration configuration,
        Action<IServiceCollection>? before = null,
        bool approvalFirst = false
    )
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();

        // Stands in for the host's ownership scheme. The TryAdd default is
        // SandboxLifecycleOwnerResolver, which pulls in the whole sandbox gateway; supplying one here
        // keeps these tests about wiring. That the default is what appears when the host supplies
        // nothing is asserted separately, against the descriptors rather than the built provider.
        _ = services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
        before?.Invoke(services);

        if (approvalFirst)
        {
            _ = services.AddRemoteToolApproval(configuration);
            _ = services.AddLifecycleDelivery(configuration);
        }
        else
        {
            _ = services.AddLifecycleDelivery(configuration);
            _ = services.AddRemoteToolApproval(configuration);
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Wires what the enabled flags promise, which <c>AddLifecycleControlPlane</c> now insists on
    /// having seen. A route-shape test cares about which controllers are published rather than about
    /// the services behind them — but "published with nothing behind it" is the exact failure that
    /// method exists to prevent, so it refuses to be the one that creates it.
    /// </summary>
    private static IServiceCollection WithLifecycleServices(WebApplicationBuilder builder, IConfiguration configuration)
    {
        _ = builder.Services.AddSingleton<ILifecycleOwnerResolver>(new NoOwnerResolver());
        _ = builder.Services.AddLifecycleDelivery(configuration);
        _ = builder.Services.AddRemoteToolApproval(configuration);
        return builder.Services;
    }

    private static WebApplicationBuilder CreateHostBuilder() =>
        WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                // Pinned so MVC's default part is this test assembly rather than the test runner's
                // executable, which is what it would otherwise load and scan.
                ApplicationName = typeof(LifecycleHostingExtensionsTests).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Development,
            }
        );

    private static IReadOnlyList<string> RouteTemplates(IServiceProvider services) =>
        [
            .. services
                .GetRequiredService<IActionDescriptorCollectionProvider>()
                .ActionDescriptors.Items.Select(action => action.AttributeRouteInfo?.Template ?? string.Empty)
                .OrderBy(template => template, StringComparer.Ordinal),
        ];

    /// <summary>Resolves nobody, which is the fail-closed answer every call site already handles.</summary>
    private sealed class NoOwnerResolver : ILifecycleOwnerResolver
    {
        public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
            LifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<LifecycleOwnerKey?>(null);

        public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
            string? threadId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<LifecycleOwnerKey?>(null);

        public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(
            string appId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<LifecycleOwnerKey?>(null);
    }

    /// <summary>A gate the host might already have installed. Never consulted here.</summary>
    private sealed class AlwaysAllowGate : IToolApprovalGate
    {
        public ValueTask<ToolApprovalVerdict> RequestApprovalAsync(
            ToolApprovalContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(ToolApprovalVerdict.Allow());
    }

    /// <summary>A host's own discovery narrowing, which this assembly must not silently widen.</summary>
    private sealed class HostControllerFeatureProvider : ControllerFeatureProvider;

    /// <summary>
    /// The same thing said the other way. MVC consults feature providers by interface, so deriving
    /// from <see cref="ControllerFeatureProvider"/> is a convenience rather than a requirement, and a
    /// host that implemented the interface directly is every bit as authoritative.
    /// </summary>
    private sealed class HostInterfaceFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            // Never runs: composition is refused before discovery, which is the point of the test.
        }
    }

    /// <summary>
    /// Collects the actions <c>AddHttpClient</c> recorded, so the primary handler can be inspected
    /// without reaching into the factory's internals or making a request.
    /// </summary>
    private sealed class HandlerBuilderProbe : HttpMessageHandlerBuilder, IDisposable
    {
        public override string? Name { get; set; }

        public override HttpMessageHandler PrimaryHandler { get; set; } = new SocketsHttpHandler();

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];

        public override HttpMessageHandler Build() => PrimaryHandler;

        public void Dispose() => PrimaryHandler.Dispose();
    }
}

/// <summary>
/// Stands in for a controller the host itself owns. It is public, and in a different assembly from
/// the lifecycle controllers, so MVC's default discovery rules apply to it — which is exactly the
/// case <see cref="LifecycleControllerFeatureProvider"/> must leave alone.
/// </summary>
[ApiController]
[Route("api/host/probe")]
public sealed class HostProbeController : ControllerBase
{
    /// <summary>Answers nothing in particular; only its route is ever examined.</summary>
    /// <returns>An empty 200.</returns>
    [HttpGet]
    public IActionResult Get() => Ok();
}
