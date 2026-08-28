using System.Collections.Concurrent;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — the subscription control plane. The three things pinned hardest here are the ones a
/// reviewer cannot verify by reading: that owner scoping is genuinely indistinguishable from
/// non-existence, that the capacity limit survives concurrent registration, and that a rotation
/// overlap opens and closes on the registry's own clock. Time is driven by hand throughout, so
/// nothing in this file waits.
/// </summary>
public sealed class LifecycleSubscriptionRegistryTests
{
    private const string AllowedHost = "callbacks.example.com";
    private const string CallbackUrl = "https://callbacks.example.com/hook";
    private const string AppA = "app-a";
    private const string AppB = "app-b";

    // A well-formed id that was never minted: long enough and hex enough to be indistinguishable
    // from a real one to anything but the registry's own table.
    private const string UnmintedId = "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly TimeSpan RotationOverlap = TimeSpan.FromMinutes(15);

    private const string Timestamp = "1750000000";
    private const string DeliveryId = "delivery-1";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event_type":"run_started"}""");

    // ---- Registration succeeds -------------------------------------------------------------

    [Fact]
    public void Registering_mints_an_id_and_a_secret_and_stamps_the_registry_clock()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);

        var grant = registry.Register(owner, AppA, Request());

        grant.Subscription.SubscriptionId.Should().NotBeNullOrWhiteSpace();
        grant.Secret.Should().HaveLength(64, "the minted key matches WebhookSigningSecret's own fallback strength");
        grant.Subscription.Owner.Should().Be(owner);
        grant.Subscription.OwnerAppId.Should().Be(AppA);
        grant.Subscription.CreatedAtUtc.Should().Be(Now, "the injected clock is the one that stamps a registration");
        registry.TryGet(owner, grant.Subscription.SubscriptionId, out var found).Should().BeTrue();
        found.Should().BeSameAs(grant.Subscription);
    }

    [Fact]
    public void Every_registration_gets_its_own_id_and_its_own_secret()
    {
        // Reusing either across subscriptions would make one subscriber's leaked key another
        // subscriber's working key.
        var registry = CreateRegistry();
        var owner = Owner(AppA);

        var first = registry.Register(owner, AppA, Request());
        var second = registry.Register(owner, AppA, Request());

        second.Subscription.SubscriptionId.Should().NotBe(first.Subscription.SubscriptionId);
        second.Secret.Should().NotBe(first.Secret);
    }

    [Fact]
    public void Both_granted_capabilities_and_a_known_event_type_are_accepted_and_retained()
    {
        var registry = CreateRegistry();

        var grant = registry.Register(
            Owner(AppA),
            AppA,
            Request(
                capabilities: [LifecycleCapabilities.ContentFull, LifecycleCapabilities.ToolApprovalDecide],
                eventTypes: [LifecycleEventTypes.RunStarted]
            )
        );

        grant.Subscription.HasCapability(LifecycleCapabilities.ContentFull).Should().BeTrue();
        grant.Subscription.HasCapability(LifecycleCapabilities.ToolApprovalDecide).Should().BeTrue();
        grant.Subscription.AcceptsEventType(LifecycleEventTypes.RunStarted).Should().BeTrue();
        grant.Subscription.AcceptsEventType(LifecycleEventTypes.RunCompleted).Should().BeFalse();
    }

    [Fact]
    public void An_empty_event_type_list_is_legal_and_means_every_type_in_the_owners_scope()
    {
        // Enumerating would silently exclude event types added in a later build, so "empty" has to
        // stay a grant rather than become a rejection.
        var registry = CreateRegistry();

        var grant = registry.Register(Owner(AppA), AppA, Request(eventTypes: []));

        grant.Subscription.EventTypes.Should().BeEmpty();
        grant.Subscription.AcceptsEventType(LifecycleEventTypes.SandboxCreated).Should().BeTrue();
    }

    [Fact]
    public void An_empty_capability_list_is_legal_and_grants_nothing()
    {
        var registry = CreateRegistry();

        var grant = registry.Register(Owner(AppA), AppA, Request(capabilities: []));

        grant.Subscription.Capabilities.Should().BeEmpty();
        grant.Subscription.HasCapability(LifecycleCapabilities.ToolApprovalDecide).Should().BeFalse();
    }

    // ---- Callback validation ---------------------------------------------------------------

    [Theory]
    [InlineData("ftp://callbacks.example.com/hook", LifecycleSubscriptionRejection.InvalidCallback)]
    [InlineData("file://callbacks.example.com/hook", LifecycleSubscriptionRejection.InvalidCallback)]
    [InlineData("https://user:pass@callbacks.example.com/hook", LifecycleSubscriptionRejection.InvalidCallback)]
    [InlineData("http://callbacks.example.com/hook", LifecycleSubscriptionRejection.CallbackNotHttps)]
    [InlineData("https://elsewhere.example.com/hook", LifecycleSubscriptionRejection.CallbackNotAllowed)]
    public void A_callback_that_fails_any_fail_closed_rule_is_refused(
        string url,
        LifecycleSubscriptionRejection expected
    )
    {
        var registry = CreateRegistry();

        ShouldReject(() => registry.Register(Owner(AppA), AppA, Request(url)), expected);
    }

    [Fact]
    public void A_relative_callback_is_refused_rather_than_dereferenced()
    {
        var registry = CreateRegistry();
        var request = new LifecycleSubscriptionRequest { CallbackUri = new Uri("/hook", UriKind.Relative) };

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, request),
            LifecycleSubscriptionRejection.InvalidCallback
        );
    }

    [Fact]
    public void A_null_callback_is_refused_rather_than_throwing_a_null_reference()
    {
        // `required Uri` is a compile-time promise, not a runtime one: a deserialized body can still
        // put a null here, and a rejection is a better answer to that than a 500.
        var registry = CreateRegistry();
        var request = new LifecycleSubscriptionRequest { CallbackUri = null! };

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, request),
            LifecycleSubscriptionRejection.InvalidCallback
        );
    }

    [Theory]
    [InlineData(CallbackUrl)]
    [InlineData("http://localhost:5051/hook")]
    public void An_empty_allow_list_refuses_every_callback(string url)
    {
        // The fail-closed posture that matters most: delivery enabled with no destination named must
        // refuse registrations, not become an open outbound relay pointed at whatever a caller sends.
        var registry = CreateRegistry(Options(requireHttps: false, allowedHosts: []));

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, Request(url)),
            LifecycleSubscriptionRejection.CallbackNotAllowed
        );
    }

    [Fact]
    public void An_allowed_host_matches_case_insensitively_because_dns_does()
    {
        var registry = CreateRegistry(Options(allowedHosts: ["CallBacks.Example.COM"]));

        var grant = registry.Register(Owner(AppA), AppA, Request());

        grant.Subscription.CallbackUri.Host.Should().Be(AllowedHost);
    }

    [Fact]
    public void Plaintext_is_accepted_only_when_the_host_has_opted_out_of_requiring_https()
    {
        // The local-development escape hatch, and the reason RequireHttpsCallbacks is a switch rather
        // than a constant.
        var registry = CreateRegistry(Options(requireHttps: false, allowedHosts: ["localhost"]));

        var grant = registry.Register(Owner(AppA), AppA, Request("http://localhost:5051/hook"));

        grant.Subscription.CallbackUri.Scheme.Should().Be(Uri.UriSchemeHttp);
    }

    // ---- Capability and event-type validation ----------------------------------------------

    [Theory]
    [InlineData("*")]
    [InlineData("lifecycle.*")]
    [InlineData("*.approval.decide")]
    public void A_wildcard_capability_is_never_granted(string capability)
    {
        var registry = CreateRegistry();

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, Request(capabilities: [capability])),
            LifecycleSubscriptionRejection.WildcardNotGranted
        );
    }

    [Theory]
    [InlineData("*")]
    [InlineData("run_*")]
    public void A_wildcard_event_type_is_never_granted(string eventType)
    {
        var registry = CreateRegistry();

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, Request(eventTypes: [eventType])),
            LifecycleSubscriptionRejection.WildcardNotGranted
        );
    }

    [Fact]
    public void An_unknown_capability_is_refused_rather_than_silently_dropped()
    {
        // Dropping it would leave the caller believing it holds a permission it was never granted,
        // and it would build on that belief far from the registration that actually failed.
        var registry = CreateRegistry();

        ShouldReject(
            () =>
                registry.Register(
                    Owner(AppA),
                    AppA,
                    Request(capabilities: [LifecycleCapabilities.ContentFull, "lifecycle.everything"])
                ),
            LifecycleSubscriptionRejection.UnknownCapability
        );
    }

    [Fact]
    public void An_unknown_event_type_is_refused()
    {
        var registry = CreateRegistry();

        ShouldReject(
            () =>
                registry.Register(
                    Owner(AppA),
                    AppA,
                    Request(eventTypes: [LifecycleEventTypes.RunStarted, "run_teleported"])
                ),
            LifecycleSubscriptionRejection.UnknownEventType
        );
    }

    [Fact]
    public void A_refused_registration_leaves_no_trace_behind()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);

        ShouldReject(
            () => registry.Register(owner, AppA, Request(capabilities: ["lifecycle.everything"])),
            LifecycleSubscriptionRejection.UnknownCapability
        );

        registry.ForOwner(owner).Should().BeEmpty("a rejected registration must not consume a slot");
    }

    // ---- Capacity ---------------------------------------------------------------------------

    [Fact]
    public void Registering_beyond_the_configured_maximum_is_refused()
    {
        var registry = CreateRegistry(Options(maxSubscriptions: 2));
        registry.Register(Owner(AppA), AppA, Request());
        registry.Register(Owner(AppB), AppB, Request());

        ShouldReject(
            () => registry.Register(Owner(AppA), AppA, Request()),
            LifecycleSubscriptionRejection.CapacityExceeded
        );
    }

    [Fact]
    public void The_capacity_limit_holds_under_concurrent_registration()
    {
        // The race a naive check-then-add loses: N threads all read the same last free slot and all
        // take it, turning a configured maximum into a suggestion. Asserted on the exact count, not
        // on "roughly the limit".
        const int Capacity = 4;
        const int Attempts = 64;
        var registry = CreateRegistry(Options(maxSubscriptions: Capacity));
        var owner = Owner(AppA);
        var refusals = new ConcurrentBag<LifecycleSubscriptionRejection>();
        var accepted = new ConcurrentBag<string>();

        Parallel.For(
            0,
            Attempts,
            _ =>
            {
                try
                {
                    accepted.Add(registry.Register(owner, AppA, Request()).Subscription.SubscriptionId);
                }
                catch (LifecycleSubscriptionRejectedException rejected)
                {
                    refusals.Add(rejected.Reason);
                }
            }
        );

        accepted.Should().HaveCount(Capacity);
        accepted
            .Distinct(StringComparer.Ordinal)
            .Should()
            .HaveCount(Capacity, "ids are unique even when minted concurrently");
        refusals.Should().HaveCount(Attempts - Capacity);
        refusals.Should().OnlyContain(reason => reason == LifecycleSubscriptionRejection.CapacityExceeded);
        registry.ForOwner(owner).Should().HaveCount(Capacity);
    }

    [Fact]
    public void Unregistering_frees_the_slot_it_held()
    {
        var registry = CreateRegistry(Options(maxSubscriptions: 1));
        var owner = Owner(AppA);
        var grant = registry.Register(owner, AppA, Request());

        registry.Unregister(owner, grant.Subscription.SubscriptionId);

        registry.Register(owner, AppA, Request()).Should().NotBeNull();
    }

    // ---- Owner scoping ----------------------------------------------------------------------

    [Fact]
    public void TryGet_refuses_another_owners_subscription_and_yields_nothing()
    {
        var registry = CreateRegistry();
        var grant = registry.Register(Owner(AppA), AppA, Request());

        registry.TryGet(Owner(AppB), grant.Subscription.SubscriptionId, out var found).Should().BeFalse();

        found.Should().BeNull("a failed lookup must not hand back the object it refused");
    }

    [Fact]
    public void Rotate_refuses_another_owners_subscription_and_leaves_its_key_alone()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var grant = registry.Register(owner, AppA, Request());

        ShouldReject(
            () => registry.Rotate(Owner(AppB), grant.Subscription.SubscriptionId),
            LifecycleSubscriptionRejection.NotAuthorized
        );

        Verifies(grant.Subscription, grant.Secret).Should().BeTrue("the refused call rotated nothing");
    }

    [Fact]
    public void RevokePreviousKey_refuses_another_owners_subscription()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var original = registry.Register(owner, AppA, Request());
        registry.Rotate(owner, original.Subscription.SubscriptionId);

        ShouldReject(
            () => registry.RevokePreviousKey(Owner(AppB), original.Subscription.SubscriptionId),
            LifecycleSubscriptionRejection.NotAuthorized
        );

        Verifies(original.Subscription, original.Secret)
            .Should()
            .BeTrue("a stranger cannot end another owner's rotation overlap");
    }

    [Fact]
    public void Unregister_refuses_another_owners_subscription_and_leaves_it_live()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var grant = registry.Register(owner, AppA, Request());

        ShouldReject(
            () => registry.Unregister(Owner(AppB), grant.Subscription.SubscriptionId),
            LifecycleSubscriptionRejection.NotAuthorized
        );

        registry.TryGet(owner, grant.Subscription.SubscriptionId, out _).Should().BeTrue();
    }

    [Fact]
    public void ForOwner_never_discloses_another_owners_subscriptions()
    {
        var registry = CreateRegistry();
        var mine = registry.Register(Owner(AppA), AppA, Request());
        registry.Register(Owner(AppB), AppB, Request());

        var forA = registry.ForOwner(Owner(AppA));

        forA.Should().ContainSingle().Which.Should().BeSameAs(mine.Subscription);
    }

    [Fact]
    public void An_owner_key_minted_separately_from_the_same_app_id_is_the_same_owner()
    {
        // The key is a record over an opaque value, so scoping survives the resolver handing back a
        // fresh instance per request rather than a cached one.
        var registry = CreateRegistry();
        var grant = registry.Register(Owner(AppA), AppA, Request());

        registry.TryGet(Owner(AppA), grant.Subscription.SubscriptionId, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(UnmintedId)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_subscription_id_is_a_refusal_rather_than_a_host_error(string subscriptionId)
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);

        registry.TryGet(owner, subscriptionId, out _).Should().BeFalse();
        ShouldReject(() => registry.Rotate(owner, subscriptionId), LifecycleSubscriptionRejection.NotAuthorized);
        ShouldReject(() => registry.Unregister(owner, subscriptionId), LifecycleSubscriptionRejection.NotAuthorized);
        ShouldReject(
            () => registry.RevokePreviousKey(owner, subscriptionId),
            LifecycleSubscriptionRejection.NotAuthorized
        );
    }

    [Fact]
    public void A_real_id_under_the_wrong_owner_is_indistinguishable_from_an_id_that_never_existed()
    {
        // The whole point of returning NotAuthorized for both: any observable difference — reason,
        // message, even phrasing — turns the control plane into an oracle for which ids are real.
        var registry = CreateRegistry();
        var mine = registry.Register(Owner(AppA), AppA, Request());
        var stranger = Owner(AppB);

        var onSomeoneElses = Record.Exception(() => registry.Rotate(stranger, mine.Subscription.SubscriptionId));
        var onNothing = Record.Exception(() => registry.Rotate(stranger, UnmintedId));

        onSomeoneElses
            .Should()
            .BeOfType<LifecycleSubscriptionRejectedException>()
            .Which.Reason.Should()
            .Be(LifecycleSubscriptionRejection.NotAuthorized);
        onNothing
            .Should()
            .BeOfType<LifecycleSubscriptionRejectedException>()
            .Which.Reason.Should()
            .Be(LifecycleSubscriptionRejection.NotAuthorized);
        onSomeoneElses!.Message.Should().Be(onNothing!.Message);
        onSomeoneElses.Message.Should().NotContain(mine.Subscription.SubscriptionId);
    }

    // ---- Rotation ---------------------------------------------------------------------------

    [Fact]
    public void Rotation_signs_with_the_new_key_and_keeps_the_previous_one_verifying_during_the_overlap()
    {
        var clock = new ManualTimeProvider(Now);
        var registry = CreateRegistry(clock: clock);
        var owner = Owner(AppA);
        var original = registry.Register(owner, AppA, Request());

        var rotated = registry.Rotate(owner, original.Subscription.SubscriptionId);

        rotated.Secret.Should().NotBe(original.Secret);
        rotated
            .Subscription.Should()
            .BeSameAs(original.Subscription, "rotation replaces the key, not the subscription");
        Verifies(rotated.Subscription, rotated.Secret).Should().BeTrue("the current key verifies");
        Verifies(rotated.Subscription, original.Secret)
            .Should()
            .BeTrue("a delivery signed just before the rotation must not be rejected");
    }

    [Fact]
    public void The_previous_key_stops_verifying_once_the_configured_overlap_lapses()
    {
        var clock = new ManualTimeProvider(Now);
        var registry = CreateRegistry(clock: clock);
        var owner = Owner(AppA);
        var original = registry.Register(owner, AppA, Request());
        var rotated = registry.Rotate(owner, original.Subscription.SubscriptionId);

        clock.Advance(RotationOverlap);

        Verifies(rotated.Subscription, original.Secret)
            .Should()
            .BeFalse("the overlap the registry configured has passed");
        Verifies(rotated.Subscription, rotated.Secret).Should().BeTrue("the current key is unaffected");
    }

    [Fact]
    public void Revoking_the_previous_key_takes_effect_without_advancing_the_clock()
    {
        // The compromise response: a leaked outgoing key has to stop verifying now, not when the
        // window it was rotated out under happens to close.
        var clock = new ManualTimeProvider(Now);
        var registry = CreateRegistry(clock: clock);
        var owner = Owner(AppA);
        var original = registry.Register(owner, AppA, Request());
        var rotated = registry.Rotate(owner, original.Subscription.SubscriptionId);

        registry.RevokePreviousKey(owner, original.Subscription.SubscriptionId);

        rotated.Subscription.SigningSecret.HasPreviousKey.Should().BeFalse();
        Verifies(rotated.Subscription, original.Secret).Should().BeFalse();
        Verifies(rotated.Subscription, rotated.Secret).Should().BeTrue();
    }

    [Fact]
    public void Rotating_one_subscription_leaves_every_other_subscribers_key_working()
    {
        // Per-subscription secrets, not a host-wide one: one subscriber's rotation must be invisible
        // to the rest.
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var first = registry.Register(owner, AppA, Request());
        var second = registry.Register(owner, AppA, Request());

        registry.Rotate(owner, first.Subscription.SubscriptionId);

        Verifies(second.Subscription, second.Secret).Should().BeTrue();
    }

    // ---- Removal ----------------------------------------------------------------------------

    [Fact]
    public void Unregistering_removes_the_subscription_from_every_lookup()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var grant = registry.Register(owner, AppA, Request());

        registry.Unregister(owner, grant.Subscription.SubscriptionId);

        registry.TryGet(owner, grant.Subscription.SubscriptionId, out _).Should().BeFalse();
        registry.ForOwner(owner).Should().BeEmpty();
        ShouldReject(
            () => registry.Rotate(owner, grant.Subscription.SubscriptionId),
            LifecycleSubscriptionRejection.NotAuthorized
        );
    }

    [Fact]
    public void ForOwner_hands_back_a_snapshot_a_concurrent_change_cannot_disturb()
    {
        var registry = CreateRegistry();
        var owner = Owner(AppA);
        var first = registry.Register(owner, AppA, Request());
        registry.Register(owner, AppA, Request());

        var snapshot = registry.ForOwner(owner);
        registry.Unregister(owner, first.Subscription.SubscriptionId);
        registry.Register(owner, AppA, Request());

        snapshot.Should().HaveCount(2, "the list handed to the fan-out is materialized, not a live view");
        snapshot.Should().Contain(first.Subscription);
        registry.ForOwner(owner).Should().HaveCount(2).And.NotContain(first.Subscription);
    }

    // ---- Diagnostics --------------------------------------------------------------------------

    [Fact]
    public void A_rejection_message_carries_neither_the_full_callback_url_nor_anything_on_it()
    {
        // ADR 0005: opaque identifiers only, never full URLs. A callback path or query is where a
        // per-subscriber token lives, so naming the host is the most a refusal may disclose.
        const string Url = "https://elsewhere.example.com/hook/v1?token=super-secret-token";
        var registry = CreateRegistry();

        var rejection = Record.Exception(() => registry.Register(Owner(AppA), AppA, Request(Url)));

        rejection!.Message.Should().NotContain(Url).And.NotContain("super-secret-token").And.NotContain("/hook");
        rejection.Message.Should().Contain("elsewhere.example.com", "an operator needs the host to fix the allow-list");
    }

    [Fact]
    public void Neither_the_subscription_nor_the_grant_renders_its_secret()
    {
        var registry = CreateRegistry();

        var grant = registry.Register(Owner(AppA), AppA, Request());

        grant.ToString().Should().NotContain(grant.Secret).And.Contain("[REDACTED]");
        grant.Subscription.ToString().Should().NotContain(grant.Secret);
        grant.Subscription.SigningSecret.ToString().Should().NotContain(grant.Secret);
    }

    // ---- Construction -------------------------------------------------------------------------

    [Fact]
    public void A_misconfigured_options_object_fails_at_construction_rather_than_at_first_use()
    {
        var options = Options();
        options.MaxSubscriptions = 0;

        var construct = () => CreateRegistry(options);

        construct.Should().Throw<InvalidOperationException>().WithMessage("*MaxSubscriptions*");
    }

    [Fact]
    public void The_registry_does_not_second_guess_the_hosts_decision_to_wire_it_up()
    {
        // Enabled is the host's switch for whether delivery runs at all; a host that constructed this
        // registry meant to. Re-checking the flag here would hand it a store that refuses everything
        // for a reason no error message would explain.
        var options = Options();
        options.Enabled = false;
        var registry = CreateRegistry(options);

        registry.Register(Owner(AppA), AppA, Request()).Should().NotBeNull();
    }

    [Fact]
    public void A_missing_owner_is_a_host_defect_rather_than_a_caller_rejection()
    {
        // The resolver returns null for "no owner" and the host must refuse before reaching here, so
        // a null arriving at the registry is a wiring bug and is reported as one.
        var registry = CreateRegistry();

        var register = () => registry.Register(null!, AppA, Request());
        var forOwner = () => registry.ForOwner(null!);

        register.Should().Throw<ArgumentNullException>();
        forOwner.Should().Throw<ArgumentNullException>();
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static LifecycleOwnerKey Owner(string appId) => LifecycleOwnerKey.ForAppId(appId);

    private static LifecycleDeliveryOptions Options(
        int maxSubscriptions = 8,
        bool requireHttps = true,
        string[]? allowedHosts = null
    ) =>
        new()
        {
            Enabled = true,
            MaxSubscriptions = maxSubscriptions,
            RequireHttpsCallbacks = requireHttps,
            AllowedCallbackHosts = allowedHosts ?? [AllowedHost],
            KeyRotationOverlap = RotationOverlap,
        };

    private static InMemoryLifecycleSubscriptionRegistry CreateRegistry(
        LifecycleDeliveryOptions? options = null,
        TimeProvider? clock = null
    ) =>
        new(
            options ?? Options(),
            NullLogger<InMemoryLifecycleSubscriptionRegistry>.Instance,
            clock ?? new ManualTimeProvider(Now)
        );

    private static LifecycleSubscriptionRequest Request(
        string callbackUrl = CallbackUrl,
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? eventTypes = null
    ) =>
        new()
        {
            CallbackUri = new Uri(callbackUrl),
            Capabilities = capabilities ?? [],
            EventTypes = eventTypes ?? [],
        };

    private static void ShouldReject(Action act, LifecycleSubscriptionRejection expected) =>
        act.Should().Throw<LifecycleSubscriptionRejectedException>().Which.Reason.Should().Be(expected);

    /// <summary>
    /// Whether a delivery signed with <paramref name="secret"/> is still accepted by the
    /// subscription's current active key set.
    /// </summary>
    private static bool Verifies(LifecycleSubscription subscription, string secret) =>
        subscription.SigningSecret.Matches(
            new WebhookSigningSecret(secret).ComputeHex(Timestamp, DeliveryId, Body),
            Timestamp,
            DeliveryId,
            Body
        );
}
