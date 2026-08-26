using System.Security.Claims;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — what revoking a subscription is understood to mean, asserted end to end.
/// <para>
/// The unit-level suites either side of this one each hold half the claim: the controller tests
/// prove the registry no longer lists the subscription, and the pipeline tests prove an abandoned
/// queue stops sending. Neither can see the gap between them, and the gap is where the interesting
/// failure lives — a subscriber whose registration is gone but whose queue still holds bodies that
/// were serialized and signed while it was live, or whose worker is sitting in a retry backoff about
/// to send one. So everything here is the real thing: the real registry, the real pipeline, the real
/// approval store, and the real controller action a caller reaches. Only the outbound sender and the
/// clock are doubles, because the alternative is a socket and a sleep.
/// </para>
/// </summary>
public sealed class LifecycleRevocationTests
{
    private const string AppA = "app-a";
    private const string AppB = "app-b";
    private const string CallbackHost = "callbacks.example.com";
    private const string Callback = $"https://{CallbackHost}/hook";
    private const string ArgumentsJson = """{"path":"/etc/hosts"}""";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public async Task Revoking_drops_the_work_already_queued_behind_an_in_flight_attempt(
        bool revoke,
        int expectedAttempts
    )
    {
        // Both halves of the theory run the same race deliberately: one attempt is parked mid-flight
        // while three more deliveries pile up behind it. The un-revoked run is what makes the revoked
        // one worth reading — without it, a regression that lost the backlog for some unrelated reason
        // would pass the assertion that matters.
        var harness = new Harness();
        var id = await harness.RegisterAsync();

        var inFlight = new TaskCompletionSource<LifecycleDeliveryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        harness.Sender.RespondWith(
            (attempt, _) =>
                attempt.Ordinal == 1
                    ? inFlight.Task
                    : Task.FromResult(LifecycleDeliveryResult.Succeeded(202))
        );

        await harness.Pipeline.StartAsync(CancellationToken.None);
        for (var sequence = 1; sequence <= 4; sequence++)
        {
            await harness.Pipeline.PublishAsync(Event(sequence));
        }

        // The pump is single-threaded, so the fourth resolution means the first three have been fanned
        // out completely — one is with the sender and the rest are queued behind it.
        await harness.Resolver.WaitForResolutionsAsync(4);
        await harness.Sender.WaitForAttemptsAsync(1);

        if (revoke)
        {
            var revoked = await harness.As(AppA).Unregister(id);
            revoked.Should().BeOfType<NoContentResult>();
        }

        inFlight.SetResult(LifecycleDeliveryResult.Succeeded(202));

        // Shutdown is the barrier: it completes the intake, drains it, and drains every subscriber
        // queue, so once it returns every delivery this pipeline was ever going to make has been made.
        await harness.StopAsync();

        harness.Sender.Attempts.Should().HaveCount(expectedAttempts);
    }

    [Fact]
    public async Task Revoking_stops_a_delivery_that_is_sitting_in_a_retry_backoff()
    {
        // The queue is empty here and the worker is asleep on a timer, which is the case an
        // unregister-only implementation is least likely to have thought about: nothing is enqueued
        // for a "stop fanning out" fix to catch, and the body is already signed.
        var harness = new Harness(options =>
        {
            // Two attempts, so a regression fails the assertion instead of hanging the drain: an
            // uncapped retry loop would keep rescheduling itself onto a clock this test has stopped
            // advancing, and a suite that wedges is worse than one that goes red.
            options.MaxAttempts = 2;
            options.RetryBaseDelay = TimeSpan.FromSeconds(1);
        });
        var id = await harness.RegisterAsync();
        harness.Sender.RespondWith(
            (_, _) => Task.FromResult(LifecycleDeliveryResult.Retryable("http_status", 503))
        );

        await harness.Pipeline.StartAsync(CancellationToken.None);
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.Sender.WaitForAttemptsAsync(1);

        // Equal jitter puts the first backoff in [base/2, base]; naming the band is what tells this
        // wait apart from the attempt timeout, which is the other timer outstanding.
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

        (await harness.As(AppA).Unregister(id)).Should().BeOfType<NoContentResult>();

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.StopAsync();

        harness.Sender.Attempts.Should().ContainSingle("the retry was abandoned, not sent");

        // And abandoning is not laundered as a quarantine. A quarantine burns a cool-off against the
        // destination, which here did nothing wrong — and the cool-off is destination-scoped, so a
        // voluntary revocation would penalize every other subscription sharing the host.
        harness.Pipeline.QuarantineCount.Should().Be(0);
    }

    [Fact]
    public async Task Revoking_an_approver_denies_what_it_was_still_being_asked_about()
    {
        // Approval is unanimous, so a revoked approver's pending requests can no longer be allowed by
        // anyone: the outcome is already settled and only the timing is open. Left alone, the gated
        // tool call blocks until expiry while holding an admission slot.
        var harness = new Harness();
        var id = await harness.RegisterAsync();

        using var ticket = harness.Approvals.TryRegister(
            LifecycleOwnerKey.ForAppId(AppA),
            Call(),
            [id]
        )!;
        ticket.Should().NotBeNull();
        ticket.Decision.IsCompleted.Should().BeFalse("nobody has answered yet");
        harness.Approvals.PendingCount.Should().Be(1);

        (await harness.As(AppA).Unregister(id)).Should().BeOfType<NoContentResult>();

        // Bounded, because the regression this guards against is precisely a decision that never
        // arrives: an unbounded await would wedge the suite instead of failing it, and a run that
        // hangs tells a reader less than one that goes red.
        var decision = await ticket.Decision.WaitAsync(TimeSpan.FromSeconds(30));
        decision.Decision.Should().Be(WireOutcomes.Denied);
        decision.SubscriptionId.Should().Be(id, "the deny is attributed to the approver that left");
        decision.RequestId.Should().Be(ticket.Request.RequestId);
        harness.Approvals.PendingCount.Should().Be(0, "the admission slot is freed, not leaked");
    }

    [Fact]
    public async Task Another_owners_revocation_neither_succeeds_nor_reaches_this_subscriber()
    {
        var harness = new Harness();
        var id = await harness.RegisterAsync();

        var inFlight = new TaskCompletionSource<LifecycleDeliveryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        harness.Sender.RespondWith(
            (attempt, _) =>
                attempt.Ordinal == 1
                    ? inFlight.Task
                    : Task.FromResult(LifecycleDeliveryResult.Succeeded(202))
        );

        await harness.Pipeline.StartAsync(CancellationToken.None);
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.Pipeline.PublishAsync(Event(2));
        await harness.Resolver.WaitForResolutionsAsync(2);
        await harness.Sender.WaitForAttemptsAsync(1);

        // The control plane refuses first, and refuses the way it refuses an id that never existed.
        var refused = await harness.As(AppB).Unregister(id);
        refused
            .Should()
            .BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status404NotFound);

        // Reached directly as well, because the controller's refusal means the pipeline's own owner
        // check is never exercised by the path above — and it is the check that has to hold if any
        // other caller is ever wired to this entry point.
        harness.Pipeline.Abandon(LifecycleOwnerKey.ForAppId(AppB), id);
        harness.Pipeline.Abandon(LifecycleOwnerKey.ForAppId(AppA), "sub-nonexistent");

        inFlight.SetResult(LifecycleDeliveryResult.Succeeded(202));
        await harness.StopAsync();

        harness.Sender.Attempts.Should().HaveCount(2);
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static LifecycleEventEnvelope Event(int sequence) =>
        new()
        {
            EventId = $"evt-{sequence}",
            EventType = LifecycleEventTypes.RunStarted,
            SourceStreamId = "thread-1",
            SourceSequence = sequence,
            ProducerEpoch = "epoch-1",
            OccurredAt = Now,
        };

    private static ToolApprovalContext Call() =>
        new()
        {
            ToolName = "write_file",
            ToolCallId = "call-1",
            ThreadId = "thread-1",
            Arguments = CanonicalToolArguments.Freeze(ArgumentsJson),
            ExpiresAt = Now.AddMinutes(5),
        };

    /// <summary>
    /// The whole control plane as a host composes it: one options instance, one registry, one
    /// pipeline, one approval store, and controllers built over them.
    /// </summary>
    private sealed class Harness
    {
        private readonly LifecycleDeliveryOptions _options;

        internal Harness(Action<LifecycleDeliveryOptions>? configure = null)
        {
            _options = new LifecycleDeliveryOptions
            {
                Enabled = true,
                AttemptTimeout = TimeSpan.FromSeconds(10),
                DeliveryDeadline = TimeSpan.FromMinutes(5),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
                AllowedCallbackHosts = [CallbackHost],
            };
            configure?.Invoke(_options);

            Subscriptions = new InMemoryLifecycleSubscriptionRegistry(
                _options,
                NullLogger<InMemoryLifecycleSubscriptionRegistry>.Instance,
                Clock
            );
            Pipeline = new LifecycleDeliveryPipeline(
                _options,
                Resolver,
                Subscriptions,
                Sender,
                new LifecycleContentRedactor(),
                Clock,
                NullLogger<LifecycleDeliveryPipeline>.Instance
            );
            Approvals = new RemoteApprovalStore(
                new RemoteApprovalOptions { Enabled = true },
                Clock,
                NullLogger<RemoteApprovalStore>.Instance
            );
        }

        internal ManualTimeProvider Clock { get; } = new(Now);

        internal RecordingOwnerResolver Resolver { get; } = new(AppA, AppB);

        internal RecordingSender Sender { get; } = new();

        internal InMemoryLifecycleSubscriptionRegistry Subscriptions { get; }

        internal LifecycleDeliveryPipeline Pipeline { get; }

        internal RemoteApprovalStore Approvals { get; }

        /// <summary>The controller as reached by a caller the host authenticated as <paramref name="appId"/>.</summary>
        internal LifecycleSubscriptionsController As(string appId) =>
            new(
                Subscriptions,
                Resolver,
                _options,
                NullLogger<LifecycleSubscriptionsController>.Instance,
                Approvals,
                Pipeline
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(
                            new ClaimsIdentity(
                                [new Claim(LifecycleAppIdentity.AppIdClaimType, appId)],
                                "test"
                            )
                        ),
                    },
                },
            };

        /// <summary>Registers through the controller, so the id under test is a server-minted one.</summary>
        internal async Task<string> RegisterAsync(string appId = AppA)
        {
            var result = await As(appId)
                .Register(new LifecycleSubscriptionRegistration { CallbackUri = Callback });

            var body = result
                .Should()
                .BeAssignableTo<ObjectResult>()
                .Which.Value.Should()
                .BeOfType<LifecycleSubscriptionResponse>()
                .Subject;
            body.SubscriptionId.Should().NotBeNullOrWhiteSpace();
            return body.SubscriptionId!;
        }

        /// <summary>
        /// Stops the pipeline, which is also the completion barrier these tests assert behind. The
        /// clock is deliberately not advanced: the drain budget is a delay on the injected clock, so
        /// leaving it still lets the drain finish on its own rather than racing its own deadline.
        /// </summary>
        internal async Task StopAsync()
        {
            await Pipeline.StopAsync(CancellationToken.None);
            Pipeline.Dispose();
        }
    }

    /// <summary>
    /// Places the app ids it was told about, and counts fan-out resolutions so a test can wait on the
    /// pump having reached a given event rather than sleeping until it probably has.
    /// </summary>
    private sealed class RecordingOwnerResolver(params string[] knownAppIds) : ILifecycleOwnerResolver
    {
        private readonly HashSet<string> _known = [.. knownAppIds];
        private readonly Gate _gate = new();
        private int _resolutions;

        internal Task WaitForResolutionsAsync(int count) =>
            _gate.WaitAsync(() => Volatile.Read(ref _resolutions) >= count);

        public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(
            string appId,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(
                _known.Contains(appId) ? LifecycleOwnerKey.ForAppId(appId) : null
            );

        public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
            LifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default
        )
        {
            _ = Interlocked.Increment(ref _resolutions);
            _gate.Signal();
            return ValueTask.FromResult<LifecycleOwnerKey?>(LifecycleOwnerKey.ForAppId(AppA));
        }

        public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
            string? threadId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    /// <summary>A sender whose answers the test dictates and whose attempts the test can wait on.</summary>
    private sealed class RecordingSender : ILifecycleDeliverySender
    {
        private readonly Gate _gate = new();
        private readonly List<string> _attempts = [];
        private readonly Lock _sync = new();

        private Func<Attempt, CancellationToken, Task<LifecycleDeliveryResult>> _respond = (_, _) =>
            Task.FromResult(LifecycleDeliveryResult.Succeeded(202));

        /// <summary>One recorded call, ordinal included so a responder can answer the first differently.</summary>
        internal sealed record Attempt(int Ordinal, string SubscriptionId);

        internal void RespondWith(
            Func<Attempt, CancellationToken, Task<LifecycleDeliveryResult>> respond
        ) => _respond = respond;

        internal IReadOnlyList<string> Attempts
        {
            get
            {
                lock (_sync)
                {
                    return [.. _attempts];
                }
            }
        }

        internal Task WaitForAttemptsAsync(int count) =>
            _gate.WaitAsync(() =>
            {
                lock (_sync)
                {
                    return _attempts.Count >= count;
                }
            });

        public Task<LifecycleDeliveryResult> SendAsync(
            LifecycleSubscription subscription,
            string deliveryId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken
        )
        {
            Attempt attempt;
            lock (_sync)
            {
                _attempts.Add(subscription.SubscriptionId);
                attempt = new Attempt(_attempts.Count, subscription.SubscriptionId);
            }

            _gate.Signal();
            return _respond(attempt, cancellationToken);
        }
    }
}
