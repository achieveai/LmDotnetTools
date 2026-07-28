using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using Microsoft.Extensions.Logging.Abstractions;
using CoreOutcomes = AchieveAi.LmDotnetTools.LmCore.Approval.ToolApprovalOutcomes;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0003 — the remote gate. One property dominates this file: <b>nothing but an explicit remote
/// allow produces an allow</b>, so every failure mode gets its own test and every one of them
/// asserts a blocking verdict with a code that names what happened. The other two properties pinned
/// here are that an approver is chosen by owner and capability rather than by being subscribed at
/// all, and that the argument text reaches only an approver entitled to see it. The clock is driven
/// by hand, so nothing here waits.
/// </summary>
public sealed class RemoteToolApprovalGateTests
{
    private const string ArgumentsJson = """{"path":"/etc/hosts"}""";
    private const string AppA = "app-a";
    private const string Callback = "https://callbacks.example.com/hook";
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly DateTimeOffset Expiry = Now.AddMinutes(5);

    // ---- The remote answer decides ------------------------------------------------------------

    [Fact]
    public async Task An_approving_decision_allows_the_call()
    {
        var harness = new Harness();
        harness.Publisher.OnPublish = r => harness.Answer(r, WireOutcomes.Allowed);

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        verdict.IsAllowed.Should().BeTrue();
        harness.Store.PendingCount.Should().Be(0, "the gate withdraws its request on the way out");
    }

    [Fact]
    public async Task A_denying_decision_blocks_and_carries_the_approver_reason()
    {
        var harness = new Harness();
        harness.Publisher.OnPublish = r => harness.Answer(r, WireOutcomes.Denied, "not in this workspace");

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.Denied);
        verdict.Reason.Should().Be("not in this workspace");
    }

    // ---- Who may be asked ----------------------------------------------------------------------

    [Fact]
    public async Task A_thread_with_no_resolvable_owner_is_blocked()
    {
        // An unscoped approval is one any approver could answer, so "the host cannot say who owns
        // this" has to mean "nobody may approve it".
        var harness = new Harness(resolver: new FakeOwnerResolver { ThreadOwner = null });

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.MissingApprover);
        harness.Publisher.Published.Should().BeEmpty("no approver is entitled to see an unscoped call");
    }

    [Fact]
    public async Task An_owner_whose_only_subscribers_cannot_decide_is_blocked()
    {
        // Being subscribed is not being an approver: without the decide capability there is nobody
        // to ask, and a call nobody can approve does not run.
        var harness = new Harness(subscribers: [Subscriber("sub-watcher", LifecycleCapabilities.ContentFull)]);

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.MissingApprover);
        harness.Publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_subscribers_holding_the_decide_capability_are_asked()
    {
        var harness = new Harness(
            subscribers:
            [
                Subscriber("sub-approver", LifecycleCapabilities.ToolApprovalDecide),
                Subscriber("sub-watcher", LifecycleCapabilities.ContentFull),
            ]
        );
        harness.Publisher.OnPublish = r => harness.Answer(r, WireOutcomes.Allowed);

        _ = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        harness
            .Publisher.Published.Select(p => p.Subscriber.SubscriptionId)
            .Should()
            .Equal("sub-approver");
    }

    [Fact]
    public async Task The_arguments_reach_only_an_approver_granted_full_content()
    {
        // Everyone else gets the hash and nothing else — enough to pin exactly what was approved,
        // without handing the argument text to a subscriber that was never granted it.
        var harness = new Harness(
            subscribers:
            [
                Subscriber("sub-full", LifecycleCapabilities.ToolApprovalDecide, LifecycleCapabilities.ContentFull),
                Subscriber("sub-hash-only", LifecycleCapabilities.ToolApprovalDecide),
            ]
        );
        harness.Publisher.OnPublish = r => harness.Answer(r, WireOutcomes.Allowed);

        _ = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        var full = harness.Published("sub-full");
        var hashOnly = harness.Published("sub-hash-only");
        full.Arguments.Should().Be(ArgumentsJson);
        hashOnly.Arguments.Should().BeNull();
        hashOnly
            .ArgumentsHash.Should()
            .Be(full.ArgumentsHash, "both approvers are deciding about the same bytes");
    }

    // ---- Every other path blocks -----------------------------------------------------------------

    [Fact]
    public async Task A_store_that_refuses_admission_blocks_as_overload()
    {
        var harness = new Harness(configure: o =>
        {
            o.MaxPendingPerOwner = 1;
            o.MaxPendingTotal = 1;
        });
        using var wedged = harness.Store.TryRegister(LifecycleOwnerKey.ForAppId(AppA), Call());

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.Overload);
        harness.Publisher.Published.Should().BeEmpty("a request that was never admitted is never sent");
    }

    [Fact]
    public async Task A_wait_that_runs_out_blocks_as_timeout()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        harness.Publisher.OnPublish = _ =>
        {
            // The caller's token already carries the effective expiry (ToolInvocationPreparer builds
            // it that way), so an expiry arrives here as a cancellation with the clock past due.
            harness.Clock.Advance(TimeSpan.FromMinutes(10));
            cts.Cancel();
            return ValueTask.CompletedTask;
        };

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), cts.Token);

        ShouldBlockWith(verdict, CoreOutcomes.Timeout);
    }

    [Fact]
    public async Task A_cancelled_run_blocks_as_cancelled_and_leaves_nothing_pending()
    {
        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        harness.Publisher.OnPublish = _ =>
        {
            cts.Cancel();
            return ValueTask.CompletedTask;
        };

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), cts.Token);

        ShouldBlockWith(verdict, CoreOutcomes.Cancelled);
        harness
            .Store.PendingCount.Should()
            .Be(0, "an abandoned wait must not leave an admission slot consumed for the process lifetime");
    }

    [Fact]
    public async Task A_faulting_owner_resolver_blocks_as_a_hook_error()
    {
        // A resolver throwing is a defect, not a denial — but it still blocks, and the code says
        // which of the two an operator is looking at.
        var harness = new Harness(
            resolver: new FakeOwnerResolver { Fault = new InvalidOperationException("resolver down") }
        );

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.HookError);
    }

    [Fact]
    public async Task A_request_that_reaches_no_approver_blocks_immediately_as_a_hook_error()
    {
        // Waiting out a five-minute expiry for an answer that provably cannot arrive would stall the
        // run for no reason.
        var harness = new Harness();
        harness.Publisher.Fault = new InvalidOperationException("callback unreachable");

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.HookError);
        harness.Store.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task A_gate_consulted_while_remote_approval_is_disabled_blocks_rather_than_passing_through()
    {
        // "Disabled" is a decision about whether to register the gate. Once it is in the list, the
        // only reading of "no remote approval configured" that is safe is "no approver exists".
        var harness = new Harness(configure: o => o.Enabled = false);

        var verdict = await harness.Gate.RequestApprovalAsync(Call(), CancellationToken.None);

        ShouldBlockWith(verdict, CoreOutcomes.MissingApprover);
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>
    /// The assertion every failure test shares: blocked, never <c>default</c>, and carrying the code
    /// that names what happened. <see cref="ToolApprovalVerdict"/> blocks when defaulted, so checking
    /// only <c>IsAllowed</c> would pass for a gate that silently returned nothing at all.
    /// </summary>
    private static void ShouldBlockWith(ToolApprovalVerdict verdict, string outcome)
    {
        verdict.IsAllowed.Should().BeFalse("no failure path may authorize a tool call");
        verdict.Should().NotBe(default(ToolApprovalVerdict), "a blocking verdict must say why it blocked");
        verdict.Outcome.Should().Be(outcome);
    }

    private static ToolApprovalContext Call() =>
        new()
        {
            ToolName = "write_file",
            ToolCallId = "call-1",
            ThreadId = "thread-1",
            Arguments = CanonicalToolArguments.Freeze(ArgumentsJson),
            ExpiresAt = Expiry,
        };

    private static LifecycleSubscription Subscriber(string id, params string[] capabilities) =>
        new(
            id,
            LifecycleOwnerKey.ForAppId(AppA),
            AppA,
            new Uri(Callback),
            new WebhookSigningSecret(Secret),
            capabilities,
            [],
            Now
        );

    /// <summary>The gate plus the doubles around it, assembled once so each test reads as one scenario.</summary>
    private sealed class Harness
    {
        public Harness(
            Action<RemoteApprovalOptions>? configure = null,
            FakeOwnerResolver? resolver = null,
            IEnumerable<LifecycleSubscription>? subscribers = null
        )
        {
            var options = new RemoteApprovalOptions { Enabled = true };
            configure?.Invoke(options);

            Store = new RemoteApprovalStore(options, Clock, NullLogger<RemoteApprovalStore>.Instance);
            Gate = new RemoteToolApprovalGate(
                options,
                Store,
                resolver ?? new FakeOwnerResolver { ThreadOwner = LifecycleOwnerKey.ForAppId(AppA) },
                new FakeSubscriptionRegistry(
                    subscribers ?? [Subscriber("sub-approver", LifecycleCapabilities.ToolApprovalDecide)]
                ),
                Clock,
                NullLogger<RemoteToolApprovalGate>.Instance,
                Publisher
            );
        }

        public ManualTimeProvider Clock { get; } = new(Now);

        public RecordingPublisher Publisher { get; } = new();

        public RemoteApprovalStore Store { get; }

        public RemoteToolApprovalGate Gate { get; }

        /// <summary>Answers a request the way a subscribed approver would, from inside the publish call.</summary>
        public ValueTask Answer(ToolApprovalRequest request, string outcome, string? reason = null)
        {
            _ = Store.Settle(
                LifecycleOwnerKey.ForAppId(AppA),
                new ToolApprovalDecision
                {
                    RequestId = request.RequestId,
                    Decision = outcome,
                    ArgumentsHash = request.ArgumentsHash,
                    Reason = reason,
                }
            );
            return ValueTask.CompletedTask;
        }

        /// <summary>The request as one particular subscriber received it.</summary>
        public ToolApprovalRequest Published(string subscriptionId) =>
            Publisher.Published.Single(p => p.Subscriber.SubscriptionId == subscriptionId).Request;
    }

    /// <summary>Captures what each subscriber was sent, and can stand in for an approver answering.</summary>
    private sealed class RecordingPublisher : IToolApprovalRequestPublisher
    {
        public List<(LifecycleSubscription Subscriber, ToolApprovalRequest Request)> Published { get; } = [];

        /// <summary>Invoked after recording, so a test can answer or cancel at that exact point.</summary>
        public Func<ToolApprovalRequest, ValueTask>? OnPublish { get; set; }

        /// <summary>Makes every delivery fail, standing in for an unreachable callback host.</summary>
        public Exception? Fault { get; set; }

        public ValueTask PublishAsync(
            LifecycleSubscription subscriber,
            ToolApprovalRequest request,
            CancellationToken cancellationToken
        )
        {
            Published.Add((subscriber, request));
            return Fault is not null ? throw Fault : OnPublish?.Invoke(request) ?? ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Answers only the question the gate asks. The event and caller resolutions belong to the
    /// delivery pipeline and the decision endpoint, and a double that quietly answered them too
    /// would hide the gate reaching for something it should not.
    /// </summary>
    private sealed class FakeOwnerResolver : ILifecycleOwnerResolver
    {
        public LifecycleOwnerKey? ThreadOwner { get; init; }

        public Exception? Fault { get; init; }

        public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
            string? threadId,
            CancellationToken cancellationToken = default
        ) => Fault is not null ? throw Fault : ValueTask.FromResult(ThreadOwner);

        public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
            LifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(
            string appId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    /// <summary>
    /// A fixed subscription set. The gate only ever fans out, so the control-plane methods stay
    /// unimplemented — if the gate ever reaches for one, the test says so rather than passing.
    /// </summary>
    private sealed class FakeSubscriptionRegistry(IEnumerable<LifecycleSubscription> subscriptions)
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
