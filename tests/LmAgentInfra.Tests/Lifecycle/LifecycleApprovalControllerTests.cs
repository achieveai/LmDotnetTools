using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — the decision endpoint. The action methods are invoked directly rather than through a
/// host, because controllers in this library are not auto-discovered by MVC: a host must add an
/// explicit application part, so a test that booted a host and expected this route to resolve would
/// fail for reasons that have nothing to do with the controller.
/// <para>
/// Two properties here are the ones a reviewer cannot confirm by reading the code. First, the four
/// unanswerable cases must be <b>byte-identical</b>, not merely equal in status — a difference in
/// wording is an oracle that tells a prober which of the four it hit. Second, identity comes from
/// the authenticated principal and from nowhere else, so a host that wired no authentication refuses
/// everything rather than defaulting to something.
/// </para>
/// </summary>
public sealed class LifecycleApprovalControllerTests
{
    private const string ArgumentsJson = """{"path":"/etc/hosts"}""";
    private const string ToolName = "write_file";
    private const string AppA = "app-a";
    private const string AppB = "app-b";
    private const string Callback = "https://callbacks.example.com/hook";
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The property names the response is allowed to have. Anything else is a disclosure.</summary>
    private static readonly string[] AllowedResponseProperties = ["request_id", "outcome", "error"];

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    // ---- The decision lands --------------------------------------------------------------------

    [Fact]
    public async Task An_owned_matching_decision_is_accepted_and_reports_the_outcome()
    {
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        var result = await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed));

        var body = ShouldRespond(result, StatusCodes.Status200OK);
        body.Outcome.Should().Be(WireOutcomes.Allowed);
        body.RequestId.Should().Be(ticket.Request.RequestId);
        body.Error.Should().BeNull();
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Allowed, "the gate is released by the endpoint");
    }

    [Fact]
    public async Task An_identical_retry_reports_the_same_outcome_rather_than_failing()
    {
        // A duplicated POST is the normal consequence of a network retry, and an approver that never
        // saw the first response must not be told its decision never landed.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);
        var decision = Decision(ticket, WireOutcomes.Denied);

        _ = await harness.As(AppA).Decide(decision);
        var retry = await harness.As(AppA).Decide(decision);

        ShouldRespond(retry, StatusCodes.Status200OK).Outcome.Should().Be(WireOutcomes.Denied);
    }

    [Fact]
    public async Task The_owner_is_resolved_from_the_principal_and_from_nothing_on_the_request()
    {
        // The decision payload has no owner field by design, so the assertion that matters is which
        // identity the resolver was handed: the authenticated one, unmodified.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        _ = await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed));

        harness.Resolver.Asked.Should().Equal(AppA);
    }

    // ---- Conflicts ------------------------------------------------------------------------------

    [Fact]
    public async Task A_decision_quoting_a_different_arguments_hash_conflicts()
    {
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        var result = await harness
            .As(AppA)
            .Decide(Decision(ticket, WireOutcomes.Allowed, hash: new string('a', 64)));

        var body = ShouldRespond(result, StatusCodes.Status409Conflict);
        body.Outcome.Should().BeNull("nothing was decided, so there is no outcome to report");
        ticket.Decision.IsCompleted.Should().BeFalse("the call is still waiting for a real answer");
    }

    [Fact]
    public async Task A_contradicting_second_decision_conflicts_and_reports_the_first_outcome()
    {
        // Returning the standing answer is what stops an approver from assuming its reversal took
        // effect; a bare 409 would leave it believing the call was denied when it was allowed.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);
        _ = await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Denied));

        var result = await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed));

        var body = ShouldRespond(result, StatusCodes.Status409Conflict);
        body.Outcome.Should().Be(WireOutcomes.Denied, "the decision in force is the first one, not the latest");
        (await ticket.Decision).Decision.Should().Be(WireOutcomes.Denied, "an allow arriving second must not overturn a deny");
    }

    // ---- Identity ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_that_wired_no_authentication_refuses_every_decision()
    {
        // The default-host case: signature verification establishes no principal, so HttpContext.User
        // is present but anonymous. Refusing is the safe direction; the point of this test is that the
        // refusal happens before the store is touched, so an anonymous caller cannot even probe it.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);
        var decision = Decision(ticket, WireOutcomes.Allowed);

        var result = await harness.AsAnonymous().Decide(decision);

        ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().BeEmpty("an unauthenticated caller never reaches owner resolution");
        harness.Store.PendingCount.Should().Be(1, "the request is untouched and still awaiting a real approver");
        ShouldRespond(await harness.As(AppA).Decide(decision), StatusCodes.Status200OK)
            .Outcome.Should()
            .Be(WireOutcomes.Allowed, "the attempt left the request decidable by its actual owner");
    }

    [Fact]
    public async Task A_request_carrying_no_principal_at_all_is_refused()
    {
        // Distinct from the anonymous case above: here nothing ever populated HttpContext.User, which
        // is what a host reaching this action outside a request pipeline looks like. The nullable
        // principal is easy to miss, and dereferencing it would turn a 403 into a 500.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        var result = await harness.WithoutHttpContext().Decide(Decision(ticket, WireOutcomes.Allowed));

        ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task An_authenticated_principal_carrying_no_app_identity_is_refused()
    {
        // Authenticated by some scheme, but with nothing that names the app. There is no safe default
        // to fall back to, so this refuses rather than resolving an owner from thin air.
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        var result = await harness.AsNamelessIdentity().Decide(Decision(ticket, WireOutcomes.Allowed));

        ShouldRespond(result, StatusCodes.Status403Forbidden);
        harness.Resolver.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task An_authenticated_caller_the_host_cannot_place_is_refused()
    {
        var harness = new Harness();
        using var ticket = harness.Register(AppA);

        var result = await harness.As("app-nobody").Decide(Decision(ticket, WireOutcomes.Allowed));

        ShouldRespond(result, StatusCodes.Status403Forbidden);

        // The resolver is asked, and the null it returns is honored. Asserted as a whole sequence
        // rather than a containment check so an extra resolution would fail here too.
        harness.Resolver.Asked.Should().Equal("app-nobody");
    }

    [Fact]
    public async Task An_owner_whose_decide_capability_was_revoked_is_refused()
    {
        // The capability is re-checked at decision time precisely because it can be withdrawn while
        // an approval is pending, and the moment that matters is the moment the decision lands.
        var harness = new Harness(subscribers: [Subscriber(AppA, "sub-a", LifecycleCapabilities.ContentFull)]);
        using var ticket = harness.Register(AppA);

        var result = await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed));

        ShouldRespond(result, StatusCodes.Status403Forbidden);
        ticket.Decision.IsCompleted.Should().BeFalse("a caller who may not decide did not decide");
    }

    // ---- One answer for everything unanswerable ---------------------------------------------------

    [Fact]
    public async Task Unknown_expired_foreign_and_stale_epoch_requests_are_byte_identical()
    {
        var harness = new Harness();
        using var ticket = harness.Register(AppA);
        var valid = Decision(ticket, WireOutcomes.Allowed);

        var answers = new List<(string Case, IActionResult Result)>
        {
            ("an id that was never registered", await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed, requestId: NeverRegistered(ticket)))),
            ("another owner's request", await harness.As(AppB).Decide(valid)),
            ("an id minted by a previous process", await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed, requestId: WithForeignEpoch(ticket.Request.RequestId)))),
        };

        // Last, because it moves the clock for every request this harness holds.
        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        answers.Add(("a request that already expired", await harness.As(AppA).Decide(valid)));

        var reference = BodyHex(answers[0].Result);
        foreach (var (name, result) in answers)
        {
            ShouldRespond(result, StatusCodes.Status404NotFound);
            BodyHex(result)
                .Should()
                .Be(reference, "'{0}' must be indistinguishable from '{1}'", name, answers[0].Case);
        }
    }

    [Fact]
    public async Task A_host_with_remote_approval_switched_off_answers_like_an_unknown_request()
    {
        // Defense in depth: the host is expected to keep this controller out of its application parts
        // when the feature is off, but a half-wired host must look like a host with nothing pending
        // rather than announce a feature it is not running.
        var enabled = new Harness();
        using var known = enabled.Register(AppA);
        var unknown = await enabled
            .As(AppA)
            .Decide(Decision(known, WireOutcomes.Allowed, requestId: NeverRegistered(known)));

        var disabled = new Harness(o => o.Enabled = false);
        using var ticket = disabled.Register(AppA);
        var result = await disabled.As(AppA).Decide(Decision(ticket, WireOutcomes.Allowed));

        ShouldRespond(result, StatusCodes.Status404NotFound);
        BodyHex(result).Should().Be(BodyHex(unknown), "a switched-off host discloses nothing by its answer");
        ticket.Decision.IsCompleted.Should().BeFalse("a valid decision is still not applied while the feature is off");
        disabled.Resolver.Asked.Should().BeEmpty("nothing is resolved for an endpoint that is not running");
    }

    [Fact]
    public async Task A_malformed_body_is_refused()
    {
        var harness = new Harness();

        var result = await harness.As(AppA).Decide(null!);

        ShouldRespond(result, StatusCodes.Status400BadRequest);
    }

    // ---- Nothing leaks back out ----------------------------------------------------------------------

    [Fact]
    public async Task No_response_on_any_path_carries_the_call_it_was_asked_about()
    {
        // Error paths are where argument text usually leaks back out, so this sweeps every branch the
        // action can take rather than only the success case. The property-name allow-list is the part
        // that keeps holding as the response type grows: a new field has to be added here to pass.
        foreach (var (name, result) in await EveryResponsePathAsync())
        {
            var json = BodyJson(result);
            using var document = JsonDocument.Parse(json);

            document
                .RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Should()
                .BeSubsetOf(AllowedResponseProperties, "'{0}' may only answer, never describe the call", name);
            json.Should().NotContain(ArgumentsJson, "'{0}' must not echo the arguments", name);
            json.Should().NotContain("/etc/hosts", "'{0}' must not echo any part of the arguments", name);
            json.Should().NotContain(ToolName, "'{0}' has no reason to name the tool", name);
        }
    }

    /// <summary>
    /// Every response this action can produce, one per branch, named for the failure message. Kept in
    /// one place so a new branch is one line here rather than a path the leak sweep silently misses.
    /// </summary>
    private static async Task<IReadOnlyList<(string Case, IActionResult Result)>> EveryResponsePathAsync()
    {
        var harness = new Harness();
        using var ticket = harness.Register(AppA);
        using var second = harness.Register(AppA);
        using var expiring = harness.Register(AppA);
        var accepted = Decision(ticket, WireOutcomes.Allowed);

        var paths = new List<(string Case, IActionResult Result)>
        {
            ("accepted", await harness.As(AppA).Decide(accepted)),
            ("identical retry", await harness.As(AppA).Decide(accepted)),
            ("contradicted", await harness.As(AppA).Decide(Decision(ticket, WireOutcomes.Denied))),
            ("mismatched hash", await harness.As(AppA).Decide(Decision(second, WireOutcomes.Allowed, hash: new string('a', 64)))),
            ("unknown id", await harness.As(AppA).Decide(Decision(second, WireOutcomes.Allowed, requestId: NeverRegistered(second)))),
            ("foreign owner", await harness.As(AppB).Decide(Decision(second, WireOutcomes.Allowed))),
            ("stale epoch", await harness.As(AppA).Decide(Decision(second, WireOutcomes.Allowed, requestId: WithForeignEpoch(second.Request.RequestId)))),
            ("unresolvable caller", await harness.As("app-nobody").Decide(Decision(second, WireOutcomes.Allowed))),
            ("unauthenticated", await harness.AsAnonymous().Decide(Decision(second, WireOutcomes.Allowed))),
            ("no principal", await harness.WithoutHttpContext().Decide(Decision(second, WireOutcomes.Allowed))),
            ("malformed body", await harness.As(AppA).Decide(null!)),
        };

        harness.Clock.Advance(TimeSpan.FromMinutes(6));
        paths.Add(("expired", await harness.As(AppA).Decide(Decision(expiring, WireOutcomes.Allowed))));

        var revoked = new Harness(subscribers: [Subscriber(AppA, "sub-a", LifecycleCapabilities.ContentFull)]);
        using var undecidable = revoked.Register(AppA);
        paths.Add(("revoked capability", await revoked.As(AppA).Decide(Decision(undecidable, WireOutcomes.Allowed))));

        var disabled = new Harness(o => o.Enabled = false);
        using var offline = disabled.Register(AppA);
        paths.Add(("feature disabled", await disabled.As(AppA).Decide(Decision(offline, WireOutcomes.Allowed))));

        return paths;
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>Asserts the status and hands back the typed body, which every path returns.</summary>
    private static ToolApprovalDecisionResponse ShouldRespond(IActionResult result, int statusCode)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(statusCode);
        return objectResult.Value.Should().BeOfType<ToolApprovalDecisionResponse>().Subject;
    }

    /// <summary>
    /// The response exactly as it would go on the wire. Serializing rather than comparing objects is
    /// the point: two bodies that differ only in a field the serializer omits are the same answer to
    /// a caller, and two that differ in wording are not.
    /// </summary>
    private static byte[] BodyUtf8(IActionResult result)
    {
        var value = result.Should().BeAssignableTo<ObjectResult>().Subject.Value;
        value.Should().NotBeNull("every path answers with a body");
        return JsonSerializer.SerializeToUtf8Bytes(value, value!.GetType());
    }

    private static string BodyHex(IActionResult result) => Convert.ToHexString(BodyUtf8(result));

    private static string BodyJson(IActionResult result) => Encoding.UTF8.GetString(BodyUtf8(result));

    private static ToolApprovalDecision Decision(
        RemoteApprovalTicket ticket,
        string outcome,
        string? hash = null,
        string? requestId = null
    ) =>
        new()
        {
            RequestId = requestId ?? ticket.Request.RequestId,
            Decision = outcome,
            ArgumentsHash = hash ?? ticket.Request.ArgumentsHash,
        };

    private static LifecycleSubscription Subscriber(string appId, string id, params string[] capabilities) =>
        new(
            id,
            LifecycleOwnerKey.ForAppId(appId),
            appId,
            new Uri(Callback),
            new WebhookSigningSecret(Secret),
            capabilities,
            [],
            Now
        );

    /// <summary>
    /// A well-formed id from this process that was never registered. The epoch prefix is taken from a
    /// real id so what makes it unanswerable is the request, not the shape.
    /// </summary>
    private static string NeverRegistered(RemoteApprovalTicket ticket)
    {
        var id = ticket.Request.RequestId;
        return id[..(id.IndexOf('.') + 1)] + new string('a', 32);
    }

    /// <summary>
    /// Rewrites the epoch segment of a real id so it could only have come from another process.
    /// Flipping the leading character keeps the id well formed and the right length.
    /// </summary>
    private static string WithForeignEpoch(string requestId)
    {
        var characters = requestId.ToCharArray();
        characters[0] = characters[0] == '0' ? '1' : '0';
        return new string(characters);
    }

    /// <summary>The controller plus the doubles around it, so each test reads as one scenario.</summary>
    private sealed class Harness
    {
        private readonly RemoteApprovalOptions _options;
        private readonly StubSubscriptionRegistry _subscriptions;

        public Harness(
            Action<RemoteApprovalOptions>? configure = null,
            IEnumerable<LifecycleSubscription>? subscribers = null
        )
        {
            _options = new RemoteApprovalOptions { Enabled = true };
            configure?.Invoke(_options);

            Store = new RemoteApprovalStore(_options, Clock, NullLogger<RemoteApprovalStore>.Instance);
            _subscriptions = new StubSubscriptionRegistry(
                subscribers
                    ??
                    [
                        Subscriber(AppA, "sub-a", LifecycleCapabilities.ToolApprovalDecide),
                        Subscriber(AppB, "sub-b", LifecycleCapabilities.ToolApprovalDecide),
                    ]
            );
        }

        public ManualTimeProvider Clock { get; } = new(Now);

        public StubOwnerResolver Resolver { get; } = new(AppA, AppB);

        public RemoteApprovalStore Store { get; }

        /// <summary>A pending approval owned by <paramref name="appId"/>, as the gate would register it.</summary>
        public RemoteApprovalTicket Register(string appId) =>
            Store.TryRegister(
                LifecycleOwnerKey.ForAppId(appId),
                new ToolApprovalContext
                {
                    ToolName = ToolName,
                    ToolCallId = "call-1",
                    ThreadId = "thread-1",
                    Arguments = CanonicalToolArguments.Freeze(ArgumentsJson),
                    ExpiresAt = Now.AddMinutes(5),
                }
            )!;

        /// <summary>A controller reached by a caller the host authenticated as <paramref name="appId"/>.</summary>
        public LifecycleApprovalController As(string appId) =>
            WithPrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, appId)], "test")));

        /// <summary>A controller reached with a principal no scheme authenticated — the default host.</summary>
        public LifecycleApprovalController AsAnonymous() =>
            WithPrincipal(new ClaimsPrincipal(new ClaimsIdentity()));

        /// <summary>A controller reached by an authenticated caller whose principal names no app.</summary>
        public LifecycleApprovalController AsNamelessIdentity() =>
            WithPrincipal(new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test", nameType: null, roleType: null)));

        /// <summary>A controller with no request context at all, so the principal is null rather than empty.</summary>
        public LifecycleApprovalController WithoutHttpContext() => NewController();

        private LifecycleApprovalController WithPrincipal(ClaimsPrincipal principal)
        {
            var controller = NewController();
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            };
            return controller;
        }

        private LifecycleApprovalController NewController() =>
            new(
                Store,
                Resolver,
                _subscriptions,
                _options,
                NullLogger<LifecycleApprovalController>.Instance
            );
    }

    /// <summary>
    /// Places the app ids it was told about and nothing else. The thread and event resolutions belong
    /// to the gate and the delivery pipeline; a double that quietly answered them too would hide the
    /// controller reaching for an identity it has no business using.
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
            return ValueTask.FromResult(
                _known.Contains(appId) ? LifecycleOwnerKey.ForAppId(appId) : null
            );
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

    /// <summary>
    /// A fixed subscription set. The controller only ever asks who may decide, so the control-plane
    /// methods stay unimplemented — if it reaches for one, the test says so rather than passing.
    /// </summary>
    private sealed class StubSubscriptionRegistry(IEnumerable<LifecycleSubscription> subscriptions)
        : ILifecycleSubscriptionRegistry
    {
        private readonly List<LifecycleSubscription> _subscriptions = [.. subscriptions];

        public IReadOnlyList<LifecycleSubscription> ForOwner(LifecycleOwnerKey owner) =>
            [
                .. _subscriptions.Where(s =>
                    string.Equals(s.Owner.Value, owner.Value, StringComparison.Ordinal)
                ),
            ];

        public LifecycleSubscriptionGrant Register(
            LifecycleOwnerKey owner,
            string ownerAppId,
            LifecycleSubscriptionRequest request
        ) => throw new NotSupportedException();

        public LifecycleSubscriptionGrant Rotate(LifecycleOwnerKey owner, string subscriptionId) =>
            throw new NotSupportedException();

        public void RevokePreviousKey(LifecycleOwnerKey owner, string subscriptionId) =>
            throw new NotSupportedException();

        public void Unregister(LifecycleOwnerKey owner, string subscriptionId) =>
            throw new NotSupportedException();

        public bool TryGet(
            LifecycleOwnerKey owner,
            string subscriptionId,
            out LifecycleSubscription? subscription
        ) => throw new NotSupportedException();
    }
}
