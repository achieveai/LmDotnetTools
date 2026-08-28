using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — the subscription control plane. As with
/// <see cref="LifecycleApprovalControllerTests"/>, the actions are invoked directly: controllers in
/// this library are not auto-discovered, so a host-booting test would fail for reasons unrelated to
/// the controller. Reachability from a real host is proven separately, in
/// <see cref="LifecycleHostingExtensionsTests"/>.
/// <para>
/// The registry underneath is the real one rather than a stub, because most of what this controller
/// does is decide which status a registry rejection deserves — and a stub that raised the rejections
/// the test author remembered would prove the mapping against a fiction.
/// </para>
/// </summary>
public sealed class LifecycleSubscriptionsControllerTests
{
    private const string AppA = "app-a";
    private const string AppB = "app-b";
    private const string CallbackHost = "callbacks.example.com";
    private const string Callback = $"https://{CallbackHost}/hook";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    /// <summary>The property names any response may carry. Anything else is a disclosure.</summary>
    private static readonly string[] AllowedResponseProperties =
    [
        "subscription_id",
        "callback_uri",
        "capabilities",
        "event_types",
        "created_at",
        "signing_secret",
        "error",
    ];

    // ---- Registration ----------------------------------------------------------------------------

    [Fact]
    public async Task A_registration_is_accepted_and_returns_the_subscription_with_its_secret()
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Register(Registration());

        var body = ShouldRespond(result, StatusCodes.Status201Created);
        body.SubscriptionId.Should().NotBeNullOrWhiteSpace();
        body.CallbackUri.Should().Be(Callback);
        body.SigningSecret.Should().NotBeNullOrWhiteSpace("the secret is readable exactly here");
        body.CreatedAt.Should().Be(Now);
        body.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_registration_lands_under_the_owner_resolved_from_the_principal()
    {
        // The registration body has no owner field by design, so what has to be asserted is that the
        // subscription became visible to the authenticated caller's owner and to no one else.
        var harness = new Harness();

        _ = await harness.As(AppA).Register(Registration());

        harness.Resolver.Asked.Should().Equal(AppA);
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA)).Should().HaveCount(1);
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppB)).Should().BeEmpty();
    }

    [Fact]
    public async Task Capabilities_and_event_types_come_back_sorted_so_the_body_does_not_vary_run_to_run()
    {
        var harness = new Harness();

        var result = await harness
            .As(AppA)
            .Register(
                Registration(
                    capabilities: [LifecycleCapabilities.ToolApprovalDecide, LifecycleCapabilities.ContentFull],
                    eventTypes: [LifecycleEventTypes.RunCompleted, LifecycleEventTypes.RunStarted]
                )
            );

        var body = ShouldRespond(result, StatusCodes.Status201Created);
        body.Capabilities.Should().Equal(LifecycleCapabilities.ContentFull, LifecycleCapabilities.ToolApprovalDecide);
        body.EventTypes.Should().Equal(LifecycleEventTypes.RunCompleted, LifecycleEventTypes.RunStarted);
    }

    [Fact]
    public async Task An_omitted_capability_and_event_type_list_registers_an_unfiltered_subscription()
    {
        // Absent is not the same as malformed: no capabilities and no filter is a legitimate
        // subscription — every event within the caller's own scope, redacted.
        var harness = new Harness();

        var result = await harness.As(AppA).Register(new LifecycleSubscriptionRegistration { CallbackUri = Callback });

        var body = ShouldRespond(result, StatusCodes.Status201Created);
        body.Capabilities.Should().BeEmpty();
        body.EventTypes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("a callback the allow-list does not admit", "https://elsewhere.example.com/hook")]
    [InlineData("a plaintext callback", $"http://{CallbackHost}/hook")]
    [InlineData("a callback carrying credentials", $"https://user:pass@{CallbackHost}/hook")]
    [InlineData("a callback that is not absolute", "/hook")]
    [InlineData("a callback that is not a URL at all", "not a url")]
    [InlineData("no callback at all", null)]
    public async Task A_callback_the_egress_rules_refuse_is_a_bad_request(string reason, string? callback)
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Register(Registration(callback: callback));

        ShouldRespond(result, StatusCodes.Status400BadRequest)
            .Error.Should()
            .NotBeNullOrWhiteSpace("{0} is refused with the registry's own wording", reason);
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("an unknown capability", "lifecycle.everything")]
    [InlineData("a wildcard capability", "*")]
    public async Task A_capability_the_registry_will_not_grant_is_a_bad_request(string reason, string capability)
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Register(Registration(capabilities: [capability]));

        _ = ShouldRespond(result, StatusCodes.Status400BadRequest);
        harness
            .Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA))
            .Should()
            .BeEmpty("{0} registers nothing at all rather than registering without it", reason);
    }

    [Fact]
    public async Task An_unknown_event_type_is_a_bad_request()
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Register(Registration(eventTypes: ["run_exploded"]));

        _ = ShouldRespond(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Reaching_the_subscription_limit_is_a_503_rather_than_a_client_error()
    {
        // Not the caller's fault and not permanent. A 4xx would tell a client to rewrite a request
        // that was fine, and it would stop retrying the one thing that could still work.
        var harness = new Harness(o => o.MaxSubscriptions = 1);
        _ = await harness.As(AppA).Register(Registration());

        var result = await harness.As(AppB).Register(Registration());

        _ = ShouldRespond(result, StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task A_malformed_body_is_refused()
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Register(null!);

        _ = ShouldRespond(result, StatusCodes.Status400BadRequest);
    }

    // ---- Rotation and revocation -----------------------------------------------------------------

    [Fact]
    public async Task Rotation_mints_a_different_secret_for_the_same_subscription()
    {
        var harness = new Harness();
        var registered = ShouldRespond(await harness.As(AppA).Register(Registration()), StatusCodes.Status201Created);

        var rotated = ShouldRespond(
            await harness.As(AppA).RotateSecret(registered.SubscriptionId!),
            StatusCodes.Status200OK
        );

        rotated
            .SubscriptionId.Should()
            .Be(registered.SubscriptionId, "rotation replaces the key, not the subscription");
        rotated.SigningSecret.Should().NotBeNullOrWhiteSpace();
        rotated.SigningSecret.Should().NotBe(registered.SigningSecret);
    }

    [Fact]
    public async Task Revoking_the_previous_key_answers_without_a_body()
    {
        var harness = new Harness();
        var registered = ShouldRespond(await harness.As(AppA).Register(Registration()), StatusCodes.Status201Created);
        _ = await harness.As(AppA).RotateSecret(registered.SubscriptionId!);

        var result = await harness.As(AppA).RevokePreviousSecret(registered.SubscriptionId!);

        _ = result.Should().BeOfType<NoContentResult>();
        harness
            .Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA))
            .Should()
            .HaveCount(1, "ending the overlap drops a key, not the subscription");
    }

    [Fact]
    public async Task Unregistering_removes_the_subscription_from_fan_out()
    {
        var harness = new Harness();
        var registered = ShouldRespond(await harness.As(AppA).Register(Registration()), StatusCodes.Status201Created);

        var result = await harness.As(AppA).Unregister(registered.SubscriptionId!);

        _ = result.Should().BeOfType<NoContentResult>();
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA)).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_owner_cannot_rotate_revoke_or_unregister_a_subscription_it_merely_knows_the_id_of()
    {
        // Holding the id is not authority. This is the property the whole control plane rests on, so
        // it is asserted against all three mutating routes rather than a representative one.
        var harness = new Harness();
        var registered = ShouldRespond(await harness.As(AppA).Register(Registration()), StatusCodes.Status201Created);
        var id = registered.SubscriptionId!;

        _ = ShouldRespond(await harness.As(AppB).RotateSecret(id), StatusCodes.Status404NotFound);
        _ = ShouldRespond(await harness.As(AppB).RevokePreviousSecret(id), StatusCodes.Status404NotFound);
        _ = ShouldRespond(await harness.As(AppB).Unregister(id), StatusCodes.Status404NotFound);

        harness
            .Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA))
            .Should()
            .HaveCount(1, "none of the three attempts touched the subscription");
    }

    // ---- One answer for everything unanswerable ---------------------------------------------------

    [Fact]
    public async Task An_unknown_id_a_foreign_id_and_a_switched_off_host_are_byte_identical()
    {
        // Three different truths, one answer. A status code or a message that told them apart would
        // turn the control plane into an oracle for which subscription ids exist.
        var harness = new Harness();
        var id = ShouldRespond(
            await harness.As(AppA).Register(Registration()),
            StatusCodes.Status201Created
        ).SubscriptionId!;

        var disabled = new Harness(o => o.Enabled = false);

        var answers = new (string Case, IActionResult Result)[]
        {
            ("an id that was never registered", await harness.As(AppA).RotateSecret("sub-nonexistent")),
            ("another owner's id", await harness.As(AppB).RotateSecret(id)),
            ("a host with delivery switched off", await disabled.As(AppA).RotateSecret(id)),
        };

        var reference = BodyHex(answers[0].Result);
        foreach (var (name, result) in answers)
        {
            _ = ShouldRespond(result, StatusCodes.Status404NotFound);
            BodyHex(result).Should().Be(reference, "'{0}' must be indistinguishable from '{1}'", name, answers[0].Case);
        }
    }

    [Fact]
    public async Task A_host_with_delivery_switched_off_registers_nothing_and_resolves_nobody()
    {
        // Defense in depth: the host is expected to keep this controller out of its application parts
        // when delivery is off, so this covers the half-wired host — which must look like a host with
        // nothing to find rather than announce a feature it is not running.
        var harness = new Harness(o => o.Enabled = false);

        var result = await harness.As(AppA).Register(Registration());

        _ = ShouldRespond(result, StatusCodes.Status404NotFound);
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA)).Should().BeEmpty();
        harness.Resolver.Asked.Should().BeEmpty("nothing is resolved for an endpoint that is not running");
    }

    // ---- Identity ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_that_wired_no_authentication_refuses_every_registration()
    {
        var harness = new Harness();

        var result = await harness.AsAnonymous().Register(Registration());

        var body = ShouldRespond(result, StatusCodes.Status403Forbidden);
        body.Error.Should()
            .Contain(
                "not authenticated",
                "with no scheme wired there is no principal at all, and this is the one case where that "
                    + "is the accurate answer - it is the partner the not-an-app refusal is distinguished "
                    + "FROM"
            );
        harness.Resolver.Asked.Should().BeEmpty("an unauthenticated caller never reaches owner resolution");
        harness.Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId(AppA)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_carrying_no_principal_at_all_is_refused()
    {
        // Distinct from the anonymous case: here nothing ever populated HttpContext.User, which is
        // what reaching this action outside a request pipeline looks like. Dereferencing the nullable
        // principal would turn a 403 into a 500.
        var harness = new Harness();

        var result = await harness.WithoutHttpContext().Register(Registration());

        _ = ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task An_authenticated_principal_carrying_no_app_identity_is_refused()
    {
        var harness = new Harness();

        var result = await harness.AsNamelessIdentity().Register(Registration());

        _ = ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_signed_in_human_may_not_register_a_subscription_or_obtain_a_secret()
    {
        // #433. A bearer handler with inbound claim mapping on puts the token's `sub` on
        // ClaimTypes.NameIdentifier, so while this action read that claim EVERY signed-in user
        // satisfied it: they registered a callback under an owner key that was their own subject id
        // and were handed a signing secret in the 201 body. The refusal must survive the caller also
        // carrying a display name — Identity.Name was the second half of the old fallback, so a test
        // forging only a name identifier would go green on half a fix.
        var harness = new Harness();

        var result = await harness.AsSignedInHuman("dir-a:alice").Register(Registration());

        var body = ShouldRespond(result, StatusCodes.Status403Forbidden);
        body.SigningSecret.Should().BeNull("a refused caller is never handed signing material");

        // And the refusal has to describe the refusal that happened. This caller AUTHENTICATED; the
        // reason they are refused is that a person is not an app. Answering "not authenticated" sends
        // whoever reads it to inspect the one part of the pipeline that is demonstrably working, and
        // leaves the real cause unnamed - which for a host that populates HttpContext.User itself and
        // forgot to stamp the app-id claim is the difference between a five-minute fix and a hunt.
        body.Error.Should()
            .Contain("does not name an application", "the message must name the reason that actually applied");
        body.Error.Should()
            .NotContain(
                "not authenticated",
                "authentication succeeded, so this is the one thing the refusal must not claim"
            );
        harness.Resolver.Asked.Should().BeEmpty("a human principal never reaches owner resolution");
        harness
            .Subscriptions.ForOwner(LifecycleOwnerKey.ForAppId("dir-a:alice"))
            .Should()
            .BeEmpty("nothing may be filed under a human's subject id as though it were an app");

        // Non-vacuity: the same registration succeeds for a caller that really does name an app, so
        // the 403 above is about who asked rather than about the registration being unacceptable.
        _ = ShouldRespond(await harness.As(AppA).Register(Registration()), StatusCodes.Status201Created);
    }

    [Fact]
    public async Task An_authenticated_caller_the_host_cannot_place_is_refused()
    {
        var harness = new Harness();

        var result = await harness.As("app-nobody").Register(Registration());

        _ = ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().Equal("app-nobody");
    }

    // ---- Nothing leaks back out --------------------------------------------------------------------

    [Fact]
    public async Task Only_the_two_routes_that_mint_a_secret_ever_return_one()
    {
        // The control plane is write-only precisely so a leaked subscription id cannot become a
        // leaked key. That guarantee is a property of the whole response surface, not of the two
        // routes that were written with it in mind, so every branch is swept.
        foreach (var (name, result) in await EveryResponsePathAsync())
        {
            var json = BodyJson(result);
            using var document = JsonDocument.Parse(json);

            document
                .RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Should()
                .BeSubsetOf(AllowedResponseProperties, "'{0}' may only answer within the known shape", name);

            var mints = name is "registered" or "rotated";
            document
                .RootElement.TryGetProperty("signing_secret", out _)
                .Should()
                .Be(mints, "'{0}' {1} the route that minted a secret", name, mints ? "is" : "is not");
        }
    }

    /// <summary>
    /// Every response with a body that these actions can produce, one per branch, named for the
    /// failure message. Kept in one place so a new branch is one line here rather than a path the
    /// leak sweep silently misses. The two 204s are absent because they carry no body to sweep.
    /// </summary>
    private static async Task<IReadOnlyList<(string Case, IActionResult Result)>> EveryResponsePathAsync()
    {
        var harness = new Harness();
        var registered = await harness.As(AppA).Register(Registration());
        var id = ShouldRespond(registered, StatusCodes.Status201Created).SubscriptionId!;

        var full = new Harness(o => o.MaxSubscriptions = 1);
        _ = await full.As(AppA).Register(Registration());

        var disabled = new Harness(o => o.Enabled = false);

        return
        [
            ("registered", registered),
            ("rotated", await harness.As(AppA).RotateSecret(id)),
            (
                "refused callback",
                await harness.As(AppA).Register(Registration(callback: "https://elsewhere.example.com/hook"))
            ),
            (
                "refused capability",
                await harness.As(AppA).Register(Registration(capabilities: ["lifecycle.everything"]))
            ),
            ("refused event type", await harness.As(AppA).Register(Registration(eventTypes: ["run_exploded"]))),
            ("malformed body", await harness.As(AppA).Register(null!)),
            ("at capacity", await full.As(AppB).Register(Registration())),
            ("unknown id", await harness.As(AppA).RotateSecret("sub-nonexistent")),
            ("foreign id", await harness.As(AppB).RotateSecret(id)),
            ("unauthenticated", await harness.AsAnonymous().Register(Registration())),
            ("no principal", await harness.WithoutHttpContext().Register(Registration())),
            ("unresolvable caller", await harness.As("app-nobody").Register(Registration())),
            ("feature disabled", await disabled.As(AppA).Register(Registration())),
        ];
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static LifecycleSubscriptionResponse ShouldRespond(IActionResult result, int statusCode)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(statusCode);
        return objectResult.Value.Should().BeOfType<LifecycleSubscriptionResponse>().Subject;
    }

    /// <summary>
    /// The response exactly as it would go on the wire. Serializing rather than comparing objects is
    /// the point: two bodies that differ only in a field the serializer omits are the same answer to
    /// a caller, and two that differ in wording are not.
    /// </summary>
    private static byte[] BodyUtf8(IActionResult result)
    {
        var value = result.Should().BeAssignableTo<ObjectResult>().Subject.Value;
        value.Should().NotBeNull("every path with a body answers with one");
        return JsonSerializer.SerializeToUtf8Bytes(value, value!.GetType());
    }

    private static string BodyHex(IActionResult result) => Convert.ToHexString(BodyUtf8(result));

    private static string BodyJson(IActionResult result) => Encoding.UTF8.GetString(BodyUtf8(result));

    private static LifecycleSubscriptionRegistration Registration(
        string? callback = Callback,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? eventTypes = null
    ) =>
        new()
        {
            CallbackUri = callback,
            Capabilities = capabilities,
            EventTypes = eventTypes,
        };

    /// <summary>The controller over a real registry, plus the doubles the host would supply.</summary>
    private sealed class Harness
    {
        private readonly LifecycleDeliveryOptions _options;

        public Harness(Action<LifecycleDeliveryOptions>? configure = null)
        {
            _options = new LifecycleDeliveryOptions { Enabled = true, AllowedCallbackHosts = [CallbackHost] };
            configure?.Invoke(_options);

            Subscriptions = new InMemoryLifecycleSubscriptionRegistry(
                _options,
                NullLogger<InMemoryLifecycleSubscriptionRegistry>.Instance,
                Clock
            );
        }

        public ManualTimeProvider Clock { get; } = new(Now);

        public StubOwnerResolver Resolver { get; } = new(AppA, AppB);

        public InMemoryLifecycleSubscriptionRegistry Subscriptions { get; }

        /// <summary>A controller reached by a caller the host authenticated as <paramref name="appId"/>.</summary>
        public LifecycleSubscriptionsController As(string appId) =>
            WithPrincipal(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(LifecycleAppIdentity.AppIdClaimType, appId)], "test"))
            );

        /// <summary>
        /// A controller reached by a signed-in <em>human</em>: authenticated by a bearer scheme, with
        /// the token's <c>sub</c> mapped onto <see cref="ClaimTypes.NameIdentifier"/> and a display
        /// name, and no app-id claim anywhere. This is the shape #433 was about.
        /// </summary>
        public LifecycleSubscriptionsController AsSignedInHuman(string subject) =>
            WithPrincipal(
                new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, subject), new Claim(ClaimTypes.Name, subject)],
                        "Bearer"
                    )
                )
            );

        /// <summary>A controller reached with a principal no scheme authenticated — the default host.</summary>
        public LifecycleSubscriptionsController AsAnonymous() =>
            WithPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));

        /// <summary>A controller reached by an authenticated caller whose principal names no app.</summary>
        public LifecycleSubscriptionsController AsNamelessIdentity() =>
            WithPrincipal(
                new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test", nameType: null, roleType: null))
            );

        /// <summary>A controller with no request context at all, so the principal is null rather than empty.</summary>
        public LifecycleSubscriptionsController WithoutHttpContext() => NewController();

        private LifecycleSubscriptionsController WithPrincipal(ClaimsPrincipal principal)
        {
            var controller = NewController();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            };
            return controller;
        }

        private LifecycleSubscriptionsController NewController() =>
            new(Subscriptions, Resolver, _options, NullLogger<LifecycleSubscriptionsController>.Instance);
    }

    /// <summary>
    /// Places the app ids it was told about and nothing else. The thread and event resolutions belong
    /// to the delivery pipeline; a double that quietly answered them too would hide the controller
    /// reaching for an identity it has no business using.
    /// </summary>
    private sealed class StubOwnerResolver(params string[] knownAppIds) : ILifecycleOwnerResolver
    {
        private readonly HashSet<string> _known = [.. knownAppIds];

        /// <summary>Every app id the controller asked about, in order.</summary>
        public List<string> Asked { get; } = [];

        public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(
            string appId,
            CancellationToken cancellationToken = default
        )
        {
            Asked.Add(appId);
            return ValueTask.FromResult(_known.Contains(appId) ? LifecycleOwnerKey.ForAppId(appId) : null);
        }

        public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
            LifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
            string? threadId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
