using System.Net;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0002 / ADR 0005 — the outbound delivery runtime. The claims under test are the ones a slow or
/// hostile subscriber could otherwise turn into a host-wide problem: publishing never blocks, an
/// unowned event is never broadcast, one subscriber's backlog never becomes another's, and shutdown
/// is bounded no matter what the far end does.
/// <para>
/// Every wait here is driven by an injected clock or by an observable pipeline effect. Nothing sleeps
/// — a test that passes because a delay happened to be long enough is a test that fails on a loaded
/// build agent.
/// </para>
/// </summary>
public sealed class LifecycleDeliveryPipelineTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public async Task Publishing_into_a_full_intake_neither_blocks_nor_throws()
    {
        // The pump is deliberately not started, so the intake can only fill. This is the shape of the
        // failure that matters: a wedged delivery runtime must cost the agent loop nothing.
        var harness = new Harness(options => options.IntakeQueueCapacity = 1);

        var publish = async () =>
        {
            for (var index = 0; index < 50; index++)
            {
                await harness.Pipeline.PublishAsync(Event(index));
            }
        };

        await publish.Should().NotThrowAsync("a lifecycle problem must never become a run failure");
        harness.Pipeline.IntakeDropCount.Should().Be(49, "one event fits and the rest are dropped");
    }

    [Fact]
    public async Task Publishing_a_null_envelope_is_ignored_rather_than_thrown()
    {
        var harness = new Harness();

        var publish = async () => await harness.Pipeline.PublishAsync(null!);

        await publish.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_event_with_no_resolvable_owner_is_dropped_and_burns_no_sequence()
    {
        // Fail-closed, and quietly: an unowned event must not be broadcast, and it must not consume a
        // sequence number either, or the first real delivery would arrive looking like a loss.
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(lifecycleEvent =>
            lifecycleEvent.EventId == "unowned" ? null : Harness.OwnerA
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0, eventId: "unowned"));
        await harness.Pipeline.PublishAsync(Event(1, eventId: "owned"));
        await harness.StopAsync();

        harness.Sender.Deliveries.Should().ContainSingle().Which.DeliverySequence.Should().Be(1);
    }

    [Fact]
    public async Task A_resolver_that_throws_drops_the_event_rather_than_broadcasting()
    {
        // A throw is a failure to answer, not an answer. Broadcasting on it is the one outcome
        // ADR 0005 rules out.
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => throw new InvalidOperationException("resolver defect"));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.StopAsync();

        harness.Sender.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_never_reaches_a_subscriber_belonging_to_another_owner()
    {
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Registry.Add(harness.Subscription("sub-b", Harness.OwnerB));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.StopAsync();

        harness
            .Sender.Attempts.Select(attempt => attempt.SubscriptionId)
            .Should()
            .Equal(["sub-a"], "the owner filter runs before any type filter or fan-out");
    }

    [Fact]
    public async Task A_subscription_only_receives_the_event_types_it_asked_for()
    {
        var harness = new Harness();
        harness.Registry.Add(
            harness.Subscription("sub-a", eventTypes: [LifecycleEventTypes.RunCompleted])
        );
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0, eventType: LifecycleEventTypes.RunStarted));
        await harness.Pipeline.PublishAsync(Event(1, eventType: LifecycleEventTypes.RunCompleted));
        await harness.StopAsync();

        harness
            .Sender.Deliveries.Select(delivery => delivery.Event.EventType)
            .Should()
            .Equal(LifecycleEventTypes.RunCompleted);
    }

    [Fact]
    public async Task Delivery_sequence_starts_at_one_and_is_independent_per_subscription()
    {
        // Two subscribers of the same owner each number from one. A shared counter would make every
        // subscriber's stream look permanently gappy.
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Registry.Add(harness.Subscription("sub-b"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        for (var index = 0; index < 3; index++)
        {
            await harness.Pipeline.PublishAsync(Event(index));
        }

        await harness.StopAsync();

        harness.SequencesFor("sub-a").Should().Equal(1L, 2L, 3L);
        harness.SequencesFor("sub-b").Should().Equal(1L, 2L, 3L);
    }

    [Fact]
    public async Task A_queue_full_drop_leaves_a_gap_rather_than_renumbering()
    {
        // The number is claimed before the enqueue is attempted, so a drop burns it. That is the
        // point: the subscriber sees 1, 2, 4 and knows it missed one. Renumbering to 1, 2, 3 would
        // hand it a tidy sequence that quietly omits an event.
        var harness = new Harness(options =>
        {
            options.MaxQueuedDeliveriesPerSubscriber = 1;
            options.MaxAttempts = 1;
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        var firstAttemptHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Sender.RespondWith(async (attempt, _) =>
        {
            if (attempt.Ordinal == 1)
            {
                await firstAttemptHeld.Task;
            }

            return LifecycleDeliveryResult.Succeeded(202);
        });

        await harness.StartAsync();

        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1); // delivery 1 is dequeued and in flight

        await harness.Pipeline.PublishAsync(Event(1)); // delivery 2 takes the only queue slot
        await harness.Pipeline.PublishAsync(Event(2)); // delivery 3 finds it full and is dropped
        await harness.Resolver.WaitForResolutionsAsync(3);

        firstAttemptHeld.SetResult();
        await harness.Sender.WaitForAttemptsAsync(2);

        await harness.Pipeline.PublishAsync(Event(3));
        await harness.StopAsync();

        harness.Pipeline.QueueDropCount.Should().Be(1);
        harness
            .SequencesFor("sub-a")
            .Should()
            .Equal([1L, 2L, 4L], "the dropped delivery burned 3");
    }

    [Fact]
    public async Task A_byte_budget_bounds_the_queue_even_when_the_count_budget_would_not()
    {
        // A count limit is not a memory limit. With room for 256 deliveries but one byte of budget,
        // nothing may be queued — which is the degenerate end of the property that matters: a large
        // event cannot be queued 256 times just because the counter allows it.
        var harness = new Harness(options =>
        {
            options.MaxQueuedDeliveriesPerSubscriber = 256;
            options.MaxQueuedBytesPerSubscriber = 1;
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.StopAsync();

        harness.Pipeline.QueueDropCount.Should().Be(1);
        harness.Sender.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retryable_failure_is_retried_with_capped_jittered_backoff()
    {
        var harness = new Harness(options =>
        {
            options.MaxAttempts = 3;
            options.RetryBaseDelay = TimeSpan.FromSeconds(1);
            options.MaxRetryDelay = TimeSpan.FromSeconds(30);
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) =>
            Task.FromResult(LifecycleDeliveryResult.Retryable("http_status", 503))
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));

        // Equal jitter puts the first backoff in [base/2, base] and the second in [base, 2·base].
        // Asserting the band rather than a value is what makes jitter testable without injecting a
        // random source that nothing in production would ever want to configure.
        await harness.Sender.WaitForAttemptsAsync(1);
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Sender.WaitForAttemptsAsync(2);
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        await harness.Sender.WaitForAttemptsAsync(3);

        // Nothing further is scheduled: the attempt cap, not the deadline, is what stops this.
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        harness.Sender.Attempts.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_retry_after_hint_is_honored_but_clamped()
    {
        // Unclamped, one 429 saying "come back in an hour" parks this subscriber's worker — and every
        // delivery queued behind it — for an hour.
        var harness = new Harness(options =>
        {
            options.MaxAttempts = 2;
            options.MaxRetryAfter = TimeSpan.FromSeconds(60);
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) =>
            Task.FromResult(
                LifecycleDeliveryResult.Retryable("http_status", 429, TimeSpan.FromHours(1))
            )
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);

        // Scheduled at exactly the clamp, not at the hour that was asked for.
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        harness.Clock.Advance(TimeSpan.FromSeconds(59));
        harness.Sender.Attempts.Should().HaveCount(1, "the clamp has not elapsed yet");

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Sender.WaitForAttemptsAsync(2);
    }

    [Fact]
    public async Task A_rejected_request_is_not_retried()
    {
        var harness = new Harness(options => options.MaxAttempts = 4);
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) =>
            Task.FromResult(LifecycleDeliveryResult.Permanent("http_status", 400))
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.StopAsync();

        harness
            .Sender.Attempts.Should()
            .HaveCount(1, "repeating a rejected request only repeats the rejection");
    }

    [Fact]
    public async Task A_retry_re_sends_identical_bytes_under_the_original_delivery_id()
    {
        // The receiver's replay cache is keyed on the delivery id. Re-identifying or re-serializing a
        // retry would present an ordinary network hiccup as a second, distinct event.
        var harness = new Harness(options =>
        {
            options.MaxAttempts = 2;
            options.RetryBaseDelay = TimeSpan.FromSeconds(1);
            options.MaxRetryDelay = TimeSpan.FromSeconds(1);
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((attempt, _) =>
            Task.FromResult(
                attempt.Ordinal == 1
                    ? LifecycleDeliveryResult.Retryable("http_status", 503)
                    : LifecycleDeliveryResult.Succeeded(202)
            )
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        await harness.Sender.WaitForAttemptsAsync(2);

        var attempts = harness.Sender.Attempts;
        attempts[1].DeliveryId.Should().Be(attempts[0].DeliveryId);
        attempts[1].Body.Should().Equal(attempts[0].Body);
    }

    [Fact]
    public async Task A_gone_endpoint_is_quarantined_without_disturbing_another_subscriber()
    {
        // The isolation claim: quarantine touches one queue and nothing else.
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-gone"));
        harness.Registry.Add(harness.Subscription("sub-healthy"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((attempt, _) =>
            Task.FromResult(
                attempt.SubscriptionId == "sub-gone"
                    ? LifecycleDeliveryResult.Gone(410)
                    : LifecycleDeliveryResult.Succeeded(202)
            )
        );

        await harness.StartAsync();
        for (var index = 0; index < 3; index++)
        {
            await harness.Pipeline.PublishAsync(Event(index));
        }

        await harness.StopAsync();

        harness
            .SequencesFor("sub-gone")
            .Should()
            .Equal([1L], "a retired endpoint is POSTed to once and then never again");
        harness.SequencesFor("sub-healthy").Should().Equal(1L, 2L, 3L);
        harness.Pipeline.QuarantineCount.Should().Be(1);
    }

    [Fact]
    public async Task A_run_of_failed_deliveries_quarantines_only_the_failing_subscription()
    {
        var harness = new Harness(options => options.MaxAttempts = 1);
        harness.Registry.Add(harness.Subscription("sub-broken"));
        harness.Registry.Add(harness.Subscription("sub-healthy"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((attempt, _) =>
            Task.FromResult(
                attempt.SubscriptionId == "sub-broken"
                    ? LifecycleDeliveryResult.Permanent("http_status", 400)
                    : LifecycleDeliveryResult.Succeeded(202)
            )
        );

        await harness.StartAsync();
        for (var index = 0; index < 8; index++)
        {
            await harness.Pipeline.PublishAsync(Event(index));
        }

        await harness.StopAsync();

        harness
            .SequencesFor("sub-broken")
            .Should()
            .HaveCount(
                LifecycleDeliveryPipeline.QuarantineAfterConsecutiveFailedDeliveries,
                "the run of failures retires the subscription mid-queue"
            );
        harness.SequencesFor("sub-healthy").Should().Equal(1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L);
    }

    // ---- Destination re-authorization ---------------------------------------------------------

    [Fact]
    public async Task Narrowing_the_allow_list_refuses_further_deliveries_and_leaves_a_visible_gap()
    {
        // The subscription is untouched — same id, same registry entry, same callback. Only the
        // configuration moved. Re-checking at enqueue is what makes an operator's containment take
        // effect on subscriptions that were admitted before it.
        var harness = new Harness();
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);

        harness.Options.AllowedCallbackHosts = [];
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.Pipeline.PublishAsync(Event(2));
        await harness.Resolver.WaitForResolutionsAsync(3);

        // Widened again, to prove the refusal was a refusal and not a quarantine: nothing has to
        // expire before delivery resumes.
        harness.Options.AllowedCallbackHosts = [Harness.CallbackHost];
        await harness.Pipeline.PublishAsync(Event(3));
        await harness.StopAsync();

        harness
            .SequencesFor("sub-a")
            .Should()
            .Equal([1L, 4L], "the two refused deliveries burned 2 and 3");
        harness.Pipeline.QueueDropCount.Should().Be(2);
        harness.Pipeline.QuarantineCount.Should().Be(0);
    }

    [Fact]
    public async Task Narrowing_the_allow_list_abandons_a_delivery_that_is_already_retrying()
    {
        // A delivery three attempts into a five-minute deadline is exactly the traffic an operator
        // means to stop. Checking only at enqueue would leave it retrying for the rest of its budget.
        var harness = new Harness(options =>
        {
            options.MaxAttempts = 3;
            options.RetryBaseDelay = TimeSpan.FromSeconds(1);
        });
        harness.Registry.Add(harness.Subscription("sub-a"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) =>
            Task.FromResult(LifecycleDeliveryResult.Retryable("http_status", 503))
        );

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);

        // Waiting for the backoff timer to exist is what proves the retry was genuinely scheduled, so
        // the absence of a second attempt below is the allow-list stopping it rather than the retry
        // never having been armed.
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        harness.Options.AllowedCallbackHosts = [];
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        // Asserted before shutdown, and on the drop rather than on the absence of an attempt: whether
        // the re-check fires or not, the woken worker does something observable, so this waits for an
        // answer instead of for a duration. Leaving it to StopAsync would turn a regression into a
        // wedged drain — the retry would keep rescheduling itself on a clock nothing is advancing.
        await harness.WaitForDropsAsync(1);
        harness.Sender.Attempts.Should().HaveCount(1, "the scheduled retry found the door closed");
        harness.Pipeline.QuarantineCount.Should().Be(0, "the destination did nothing wrong");

        await harness.StopAsync();
    }

    [Fact]
    public async Task Requiring_https_stops_delivering_to_a_plaintext_destination_already_admitted()
    {
        // The loopback-plaintext escape hatch is the reason RequireHttpsCallbacks is a switch at all.
        // Turning it back on has to reach subscriptions admitted while it was off, or the switch only
        // protects hosts that never used it.
        var harness = new Harness(options => options.RequireHttpsCallbacks = false);
        harness.Registry.Add(
            harness.Subscription("sub-a", callbackUri: new Uri($"http://{Harness.CallbackHost}/hook"))
        );
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);

        harness.Options.RequireHttpsCallbacks = true;
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-a").Should().Equal([1L], "the second delivery burned 2 and was refused");
        harness.Pipeline.QueueDropCount.Should().Be(1);
    }

    // ---- Destination-scoped quarantine ---------------------------------------------------------

    [Fact]
    public async Task A_quarantined_destination_holds_a_subscription_registered_after_it()
    {
        // Subscription ids are server-minted and unique, so a quarantine held against the id would
        // last exactly until the client retried — and retrying on failure is ordinary client
        // behavior, not an attack. Held against the destination, the new id inherits it.
        var harness = new Harness(options => options.QuarantineCooloff = TimeSpan.FromMinutes(15));
        var callback = new Uri($"https://{Harness.CallbackHost}/hook");
        harness.Registry.Add(harness.Subscription("sub-first", callbackUri: callback));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        // Same endpoint, brand-new id, and a sender that would now happily accept the delivery — so
        // nothing but the quarantine can account for the silence.
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Registry.Remove("sub-first");
        harness.Registry.Add(harness.Subscription("sub-second", callbackUri: callback));

        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-second").Should().BeEmpty();
        harness.Sender.Attempts.Should().ContainSingle().Which.SubscriptionId.Should().Be("sub-first");
    }

    [Fact]
    public async Task A_quarantined_destination_is_released_once_the_cooloff_has_elapsed()
    {
        // Bounded rather than permanent: an endpoint that has genuinely been repaired comes back by
        // re-registering, not by restarting the host.
        var harness = new Harness(options => options.QuarantineCooloff = TimeSpan.FromMinutes(15));
        var callback = new Uri($"https://{Harness.CallbackHost}/hook");
        harness.Registry.Add(harness.Subscription("sub-first", callbackUri: callback));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Registry.Remove("sub-first");
        harness.Registry.Add(harness.Subscription("sub-repaired", callbackUri: callback));

        harness.Clock.Advance(TimeSpan.FromMinutes(15));
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-repaired").Should().Equal(1L);
    }

    [Fact]
    public async Task A_quarantine_is_scoped_to_the_owner_that_earned_it()
    {
        // Two tenants may legitimately publish to the same host. Keying quarantine on the destination
        // alone would let either of them silence the other by pointing at it and failing.
        var harness = new Harness(options => options.QuarantineCooloff = TimeSpan.FromMinutes(15));
        var callback = new Uri($"https://{Harness.CallbackHost}/hook");
        harness.Registry.Add(harness.Subscription("sub-a", callbackUri: callback));
        harness.Registry.Add(
            harness.Subscription("sub-b", owner: Harness.OwnerB, callbackUri: callback)
        );
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Resolver.ResolveWith(_ => Harness.OwnerB);
        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-a").Should().Equal([1L], "owner A's endpoint is retired");
        harness.SequencesFor("sub-b").Should().Equal([1L], "owner B never touched it");
    }

    [Fact]
    public async Task A_quarantine_follows_the_endpoint_across_paths_but_not_across_ports()
    {
        // Quarantine tracks the endpoint that is down, not the URL that happened to be dialled.
        // Keying on the full URL would let a subscriber walk away from one by appending a query
        // string; keying on the host alone would let one dead port retire a healthy service.
        var harness = new Harness(options => options.QuarantineCooloff = TimeSpan.FromMinutes(15));
        harness.Registry.Add(
            harness.Subscription(
                "sub-dead",
                callbackUri: new Uri($"https://{Harness.CallbackHost}/hook?tenant=1")
            )
        );
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Registry.Remove("sub-dead");
        harness.Registry.Add(
            harness.Subscription(
                "sub-same-endpoint",
                callbackUri: new Uri($"https://{Harness.CallbackHost}/a-different-path")
            )
        );
        harness.Registry.Add(
            harness.Subscription(
                "sub-other-port",
                callbackUri: new Uri($"https://{Harness.CallbackHost}:8443/hook")
            )
        );

        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-same-endpoint").Should().BeEmpty("only the path differs");
        harness.SequencesFor("sub-other-port").Should().Equal(1L);
    }

    [Fact]
    public async Task A_quarantine_survives_respelling_the_host_in_punycode()
    {
        // The caller chooses how to spell the host; the quarantine must not. Keying on Uri.Host
        // rather than Uri.IdnHost would make bücher.invalid and xn--bcher-kva.invalid two
        // destinations, and re-registering under the other spelling would escape the quarantine.
        // (The allow-list below still names both spellings, because that list is written by the
        // operator, who should not have to guess which one a caller will send.)
        const string Unicode = "bücher.invalid";
        const string Punycode = "xn--bcher-kva.invalid";

        var harness = new Harness(options =>
        {
            options.AllowedCallbackHosts = [Unicode, Punycode];
            options.QuarantineCooloff = TimeSpan.FromMinutes(15);
        });
        harness.Registry.Add(
            harness.Subscription("sub-unicode", callbackUri: new Uri($"https://{Unicode}/hook"))
        );
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Registry.Remove("sub-unicode");
        harness.Registry.Add(
            harness.Subscription("sub-punycode", callbackUri: new Uri($"https://{Punycode}/hook"))
        );

        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-punycode").Should().BeEmpty();
    }

    [Fact]
    public async Task A_zero_cooloff_quarantines_the_queue_without_holding_the_destination()
    {
        // Zero is the documented opt-out, and it has to mean the pre-cool-off behavior exactly:
        // the failing queue is still retired, but the endpoint is free the moment a new subscription
        // points at it.
        var harness = new Harness(options => options.QuarantineCooloff = TimeSpan.Zero);
        var callback = new Uri($"https://{Harness.CallbackHost}/hook");
        harness.Registry.Add(harness.Subscription("sub-first", callbackUri: callback));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Gone(410)));

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.WaitForQuarantinesAsync(1);

        harness.Sender.RespondWith((_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202)));
        harness.Registry.Remove("sub-first");
        harness.Registry.Add(harness.Subscription("sub-second", callbackUri: callback));

        await harness.Pipeline.PublishAsync(Event(1));
        await harness.StopAsync();

        harness.SequencesFor("sub-second").Should().Equal(1L);
    }

    [Fact]
    public async Task Shutdown_completes_within_its_drain_budget_against_a_hung_sender()
    {
        // A subscriber that never answers must not be able to hold the process open. The bound is
        // proved on the injected clock: advancing exactly the drain timeout is what releases stop.
        var harness = new Harness(options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(5));
        harness.Registry.Add(harness.Subscription("sub-hung"));
        harness.Resolver.ResolveWith(_ => Harness.OwnerA);
        harness.Sender.RespondWith(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return LifecycleDeliveryResult.Succeeded(202);
        });

        await harness.StartAsync();
        await harness.Pipeline.PublishAsync(Event(0));
        await harness.Sender.WaitForAttemptsAsync(1);

        var stop = harness.Pipeline.StopAsync(CancellationToken.None);
        await harness.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        stop.IsCompleted.Should().BeFalse("the drain budget has not elapsed yet");

        harness.Clock.Advance(TimeSpan.FromSeconds(5));

        await stop;
    }

    private static LifecycleEventEnvelope Event(
        int sequence,
        string? eventId = null,
        string? eventType = null
    ) =>
        new()
        {
            EventId = eventId ?? $"evt-{sequence}",
            EventType = eventType ?? LifecycleEventTypes.RunStarted,
            SourceStreamId = "thread-1",
            SourceSequence = sequence,
            ProducerEpoch = "epoch-1",
            OccurredAt = Origin,
        };

    /// <summary>
    /// Wires a pipeline over test doubles and exposes the two things assertions need: what reached
    /// the wire, and a clock that can be moved.
    /// </summary>
    private sealed class Harness
    {
        /// <summary>
        /// Host every subscription in this harness calls back to. Named rather than repeated so the
        /// allow-list and the URLs it admits cannot drift apart.
        /// </summary>
        internal const string CallbackHost = "subscriber.invalid";

        internal static readonly LifecycleOwnerKey OwnerA = LifecycleOwnerKey.ForAppId("app-a");
        internal static readonly LifecycleOwnerKey OwnerB = LifecycleOwnerKey.ForAppId("app-b");

        internal Harness(Action<LifecycleDeliveryOptions>? configure = null)
        {
            Options = new LifecycleDeliveryOptions
            {
                Enabled = true,
                AttemptTimeout = TimeSpan.FromSeconds(10),
                DeliveryDeadline = TimeSpan.FromMinutes(5),
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),

                // Every callback this harness mints points at CallbackHost, and the pipeline
                // re-authorizes the destination at enqueue and again before each attempt. Left at the
                // default empty list — which allows nothing — no test here would deliver anything, and
                // the whole suite would agree on the wrong answer for the wrong reason.
                AllowedCallbackHosts = [CallbackHost],
            };
            configure?.Invoke(Options);

            Clock = new ManualTimeProvider(Origin);
            Pipeline = new LifecycleDeliveryPipeline(
                Options,
                Resolver,
                Registry,
                Sender,
                new LifecycleContentRedactor(),
                Clock,
                NullLogger<LifecycleDeliveryPipeline>.Instance
            );
        }

        /// <summary>
        /// The live options instance the pipeline holds, so a test can narrow the egress rules
        /// mid-run the way an operator would — which is the only way to exercise the re-checks that
        /// exist precisely because configuration outlives the subscriptions it admitted.
        /// </summary>
        internal LifecycleDeliveryOptions Options { get; }

        internal ManualTimeProvider Clock { get; }

        internal FakeOwnerResolver Resolver { get; } = new();

        internal FakeSubscriptionRegistry Registry { get; } = new();

        internal RecordingSender Sender { get; } = new();

        internal LifecycleDeliveryPipeline Pipeline { get; }

        internal LifecycleSubscription Subscription(
            string subscriptionId,
            LifecycleOwnerKey? owner = null,
            IEnumerable<string>? capabilities = null,
            IEnumerable<string>? eventTypes = null,
            Uri? callbackUri = null
        ) =>
            new(
                subscriptionId,
                owner ?? OwnerA,
                (owner ?? OwnerA).Value,
                callbackUri ?? new Uri($"https://{CallbackHost}/{subscriptionId}"),
                new WebhookSigningSecret($"secret-for-{subscriptionId}-0123456789"),
                capabilities ?? [LifecycleCapabilities.ContentFull],
                eventTypes ?? [],
                Origin
            );

        internal Task StartAsync() => Pipeline.StartAsync(CancellationToken.None);

        /// <summary>
        /// Stops the pipeline and, in doing so, acts as the tests' completion barrier: shutdown
        /// completes the intake, drains it, then drains every subscriber queue, so once this returns
        /// every delivery the pipeline was ever going to make has been made.
        /// <para>
        /// The clock is deliberately <em>not</em> advanced here. The drain budget is a
        /// <c>Task.Delay</c> on the injected clock, so leaving the clock still means the budget never
        /// expires and the drain is allowed to finish on its own — which is what makes the assertions
        /// deterministic rather than a race between the drain and its own deadline. Tests that need
        /// the budget to expire (shutdown against a hung sender) drive that explicitly.
        /// </para>
        /// </summary>
        internal async Task StopAsync()
        {
            await Pipeline.StopAsync(CancellationToken.None);
            Pipeline.Dispose();
        }

        internal IReadOnlyList<long> SequencesFor(string subscriptionId) =>
            [
                .. Sender
                    .Attempts.Where(attempt => attempt.SubscriptionId == subscriptionId)
                    .Select(attempt =>
                        LifecycleSerializer.DeserializeDelivery(attempt.Body).DeliverySequence
                    ),
            ];

        /// <summary>
        /// Completes once <paramref name="count"/> quarantines have been imposed.
        /// </summary>
        internal Task WaitForQuarantinesAsync(int count) =>
            WaitForAsync(() => Pipeline.QuarantineCount >= count, $"{count} quarantine(s)");

        /// <summary>
        /// Completes once <paramref name="count"/> deliveries have been dropped — by a full queue or
        /// by a destination the configuration no longer admits, which share a counter because they
        /// are the same thing to a subscriber: a burned <c>delivery_sequence</c>.
        /// </summary>
        internal Task WaitForDropsAsync(int count) =>
            WaitForAsync(() => Pipeline.QueueDropCount >= count, $"{count} dropped deliver(ies)");

        /// <summary>
        /// A condition wait, not a sleep: it returns as soon as the effect is visible and can only end
        /// in the timeout if the effect never happens at all, so a loaded build agent makes it slower
        /// rather than red. Polled rather than signalled because these two counters are written by a
        /// subscriber worker with no notification of its own, and the alternative — growing a
        /// test-only signal in the pipeline — buys nothing the counter does not already give.
        /// <para>
        /// Preferred over letting <see cref="StopAsync"/> serve as the barrier wherever a regression
        /// would leave a delivery retrying: the drain budget runs on the injected clock, so a delivery
        /// that keeps rescheduling itself hangs shutdown rather than failing an assertion, and a
        /// suite that wedges is worse than one that goes red.
        /// </para>
        /// </summary>
        private static async Task WaitForAsync(Func<bool> condition, string description)
        {
            var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!condition())
            {
                if (DateTime.UtcNow > giveUp)
                {
                    throw new TimeoutException($"Timed out waiting for {description}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }
        }
    }

    /// <summary>Owner resolution under the test's control, plus a barrier on how many events the
    /// pump has begun dispatching.</summary>
    private sealed class FakeOwnerResolver : ILifecycleOwnerResolver
    {
        private readonly Gate _gate = new();
        private Func<LifecycleEventEnvelope, LifecycleOwnerKey?> _resolve = _ => null;
        private int _resolutions;

        internal void ResolveWith(Func<LifecycleEventEnvelope, LifecycleOwnerKey?> resolve) =>
            _resolve = resolve;

        /// <summary>
        /// Completes once the pump has begun dispatching <paramref name="count"/> events. Because the
        /// pump is single-threaded, that also means event <c>count - 1</c> has been fanned out
        /// completely — which is the only ordering guarantee the queue-pressure tests need.
        /// </summary>
        internal Task WaitForResolutionsAsync(int count) =>
            _gate.WaitAsync(() => Volatile.Read(ref _resolutions) >= count);

        public ValueTask<LifecycleOwnerKey?> ResolveEventOwnerAsync(
            LifecycleEventEnvelope lifecycleEvent,
            CancellationToken cancellationToken = default
        )
        {
            _ = Interlocked.Increment(ref _resolutions);
            _gate.Signal();
            return ValueTask.FromResult(_resolve(lifecycleEvent));
        }

        public ValueTask<LifecycleOwnerKey?> ResolveThreadOwnerAsync(
            string? threadId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask<LifecycleOwnerKey?> ResolveCallerAsync(
            string appId,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    /// <summary>Fan-out lookup only. The control-plane members are not part of this slice and throw
    /// rather than pretend.
    /// <para>
    /// Guarded because tests mutate it while the pump is running: re-registering a callback after a
    /// failure is ordinary client behavior and one of the things under test, so the fake has to model
    /// it rather than assume every subscription exists before the pipeline starts.
    /// </para>
    /// </summary>
    private sealed class FakeSubscriptionRegistry : ILifecycleSubscriptionRegistry
    {
        private readonly List<LifecycleSubscription> _subscriptions = [];
        private readonly Lock _sync = new();

        internal void Add(LifecycleSubscription subscription)
        {
            lock (_sync)
            {
                _subscriptions.Add(subscription);
            }
        }

        /// <summary>Drops a subscription, as a client abandoning one before re-registering would.</summary>
        internal void Remove(string subscriptionId)
        {
            lock (_sync)
            {
                _ = _subscriptions.RemoveAll(candidate =>
                    string.Equals(candidate.SubscriptionId, subscriptionId, StringComparison.Ordinal)
                );
            }
        }

        public IReadOnlyList<LifecycleSubscription> ForOwner(LifecycleOwnerKey owner)
        {
            lock (_sync)
            {
                return [.. _subscriptions.Where(subscription => subscription.Owner == owner)];
            }
        }

        public bool TryGet(
            LifecycleOwnerKey owner,
            string subscriptionId,
            out LifecycleSubscription? subscription
        )
        {
            lock (_sync)
            {
                subscription = _subscriptions.FirstOrDefault(candidate =>
                    candidate.Owner == owner
                    && string.Equals(
                        candidate.SubscriptionId,
                        subscriptionId,
                        StringComparison.Ordinal
                    )
                );
            }

            return subscription is not null;
        }

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
    }

    /// <summary>One recorded delivery attempt, captured exactly as the pipeline handed it over.</summary>
    internal sealed record RecordedAttempt(
        int Ordinal,
        string SubscriptionId,
        string DeliveryId,
        byte[] Body
    );

    /// <summary>A sender whose answers the test dictates and whose attempts the test can wait on.</summary>
    private sealed class RecordingSender : ILifecycleDeliverySender
    {
        private readonly Gate _gate = new();
        private readonly List<RecordedAttempt> _attempts = [];
        private readonly Lock _sync = new();

        private Func<RecordedAttempt, CancellationToken, Task<LifecycleDeliveryResult>> _respond =
            (_, _) => Task.FromResult(LifecycleDeliveryResult.Succeeded(202));

        internal void RespondWith(
            Func<RecordedAttempt, CancellationToken, Task<LifecycleDeliveryResult>> respond
        ) => _respond = respond;

        internal IReadOnlyList<RecordedAttempt> Attempts
        {
            get
            {
                lock (_sync)
                {
                    return [.. _attempts];
                }
            }
        }

        internal IReadOnlyList<LifecycleDeliveryEnvelope> Deliveries =>
            [.. Attempts.Select(attempt => LifecycleSerializer.DeserializeDelivery(attempt.Body))];

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
            RecordedAttempt attempt;
            lock (_sync)
            {
                attempt = new RecordedAttempt(
                    _attempts.Count + 1,
                    subscription.SubscriptionId,
                    deliveryId,
                    body.ToArray()
                );
                _attempts.Add(attempt);
            }

            _gate.Signal();
            return _respond(attempt, cancellationToken);
        }
    }

}

/// <summary>
/// ADR 0005 — the outbound transport. The claim that matters most is negative: a lifecycle callback
/// points at a third party, so the host's own credentials must never ride along on it.
/// </summary>
public sealed class HttpLifecycleDeliverySenderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly byte[] Body = """{"delivery_id":"d-1"}"""u8.ToArray();

    [Fact]
    public async Task Only_the_content_type_and_signature_headers_ever_leave()
    {
        // Both credentials are set before the sender is constructed — the route by which a shared or
        // pre-configured client would otherwise donate them to a subscriber's endpoint.
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer host-token");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Sbx-Session", "sandbox-session-1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Sbx-App", "sandbox-app-1");

        using var sender = new HttpLifecycleDeliverySender(
            client,
            new ManualTimeProvider(Now),
            NullLogger<HttpLifecycleDeliverySender>.Instance
        );

        _ = await sender.SendAsync(Subscription(), "delivery-1", Body, CancellationToken.None);

        handler
            .RequestHeaderNames.Should()
            .BeEquivalentTo(
                [
                    WebhookHeaderNames.Signature,
                    WebhookHeaderNames.Timestamp,
                    WebhookHeaderNames.DeliveryId,
                ],
                "the delivery carries its own signature and nothing the host was holding"
            );
        handler.ContentHeaderNames.Should().BeEquivalentTo(["Content-Type"]);
    }

    [Fact]
    public async Task A_delivery_is_signed_under_the_delivery_id_it_was_given()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var sender = new HttpLifecycleDeliverySender(
            new HttpClient(handler),
            new ManualTimeProvider(Now),
            NullLogger<HttpLifecycleDeliverySender>.Instance
        );

        _ = await sender.SendAsync(Subscription(), "delivery-1", Body, CancellationToken.None);

        handler.DeliveryIdHeader.Should().Be("delivery-1");
        handler.SentBody.Should().Equal(Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, LifecycleDeliveryOutcome.Succeeded)]
    [InlineData(HttpStatusCode.RequestTimeout, LifecycleDeliveryOutcome.Retryable)]
    [InlineData(HttpStatusCode.TooManyRequests, LifecycleDeliveryOutcome.Retryable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, LifecycleDeliveryOutcome.Retryable)]
    [InlineData(HttpStatusCode.BadRequest, LifecycleDeliveryOutcome.Permanent)]
    [InlineData(HttpStatusCode.Unauthorized, LifecycleDeliveryOutcome.Permanent)]
    [InlineData(HttpStatusCode.Gone, LifecycleDeliveryOutcome.Gone)]
    public async Task A_status_code_is_classified_by_whether_repeating_it_could_help(
        HttpStatusCode status,
        LifecycleDeliveryOutcome expected
    )
    {
        using var sender = new HttpLifecycleDeliverySender(
            new HttpClient(new RecordingHandler(status)),
            new ManualTimeProvider(Now),
            NullLogger<HttpLifecycleDeliverySender>.Instance
        );

        var result = await sender.SendAsync(
            Subscription(),
            "delivery-1",
            Body,
            CancellationToken.None
        );

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task A_retry_after_header_is_surfaced_for_the_pipeline_to_clamp()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests)
        {
            RetryAfterSeconds = 3600,
        };
        using var sender = new HttpLifecycleDeliverySender(
            new HttpClient(handler),
            new ManualTimeProvider(Now),
            NullLogger<HttpLifecycleDeliverySender>.Instance
        );

        var result = await sender.SendAsync(
            Subscription(),
            "delivery-1",
            Body,
            CancellationToken.None
        );

        // Reported verbatim. Deciding what to do about an hour-long hint is the pipeline's job, not
        // the transport's.
        result.RetryAfter.Should().Be(TimeSpan.FromHours(1));
    }

    private static LifecycleSubscription Subscription() =>
        new(
            "sub-a",
            LifecycleOwnerKey.ForAppId("app-a"),
            "app-a",
            new Uri("https://subscriber.invalid/hook"),
            new WebhookSigningSecret("test-signing-secret-0123456789"),
            [],
            [],
            Now
        );

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        internal IReadOnlyList<string> RequestHeaderNames { get; private set; } = [];

        internal IReadOnlyList<string> ContentHeaderNames { get; private set; } = [];

        internal string? DeliveryIdHeader { get; private set; }

        internal byte[] SentBody { get; private set; } = [];

        internal int? RetryAfterSeconds { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestHeaderNames = [.. request.Headers.Select(header => header.Key)];
            ContentHeaderNames = [.. request.Content!.Headers.Select(header => header.Key)];
            DeliveryIdHeader = request.Headers.TryGetValues(
                WebhookHeaderNames.DeliveryId,
                out var values
            )
                ? values.Single()
                : null;
            SentBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);

            var response = new HttpResponseMessage(status);
            if (RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(seconds)
                );
            }

            return response;
        }
    }
}
