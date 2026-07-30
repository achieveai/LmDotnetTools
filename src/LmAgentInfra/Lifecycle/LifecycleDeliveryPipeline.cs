using System.Collections.Concurrent;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Fans lifecycle events out to a tenant's subscribers over signed HTTP (ADR 0002, ADR 0005).
/// <para>
/// The whole design answers one question: what happens when a subscriber is slow? The agent loop
/// calls <see cref="PublishAsync"/> and that call must cost a bounded, tiny amount of work no matter
/// what any subscriber is doing — so it is a single non-blocking write into an intake channel and
/// nothing else. Everything expensive (owner resolution, redaction, serialization, HTTP, retry)
/// happens on background workers behind that channel. Back-pressure is never propagated to the
/// producer; when a queue is full the event is <em>dropped</em>, deliberately, and the drop is
/// observable to the subscriber as a gap in <c>source_sequence</c> or <c>delivery_sequence</c>.
/// This is a notification stream, not an audit log — ADR 0002 chose losing an event over slowing an
/// agent, and the alternative is a wedged subscriber able to stall every conversation on the host.
/// </para>
/// <para>
/// Each subscription gets its own bounded queue and its own worker: a bulkhead. One endpoint that
/// times out on every request delays only its own deliveries, and a quarantined endpoint stops
/// receiving without any other subscriber noticing.
/// </para>
/// </summary>
public sealed class LifecycleDeliveryPipeline : ILifecyclePublisher, IHostedService, IDisposable
{
    /// <summary>
    /// How many consecutive failed deliveries retire a subscription, absent an explicit HTTP 410.
    /// <para>
    /// A <c>const</c> rather than an option only because
    /// <see cref="LifecycleDeliveryOptions"/> has no field for it; it belongs there. The value is
    /// chosen so a short outage cannot quarantine a healthy subscriber — each of these five
    /// deliveries has already exhausted its own retry budget — while an endpoint that has been
    /// misconfigured or decommissioned stops being POSTed to within a few events rather than
    /// forever.
    /// </para>
    /// </summary>
    public const int QuarantineAfterConsecutiveFailedDeliveries = 5;

    /// <summary>
    /// Minimum gap between drop warnings. Drops arrive in bursts by their nature — the queue is full
    /// because the system is already struggling — and logging every one turns an overload into a
    /// second, self-inflicted overload. The suppressed count is carried into the next line so the
    /// magnitude is never lost.
    /// </summary>
    private static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(30);

    private readonly LifecycleDeliveryOptions _options;
    private readonly ILifecycleOwnerResolver _ownerResolver;
    private readonly ILifecycleSubscriptionRegistry _registry;
    private readonly ILifecycleDeliverySender _sender;
    private readonly LifecycleContentRedactor _redactor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LifecycleDeliveryPipeline> _logger;

    private readonly Channel<LifecycleEventEnvelope> _intake;
    private readonly ConcurrentDictionary<string, SubscriberQueue> _subscribers = new(
        StringComparer.Ordinal
    );

    /// <summary>
    /// When each quarantined destination becomes eligible again, keyed by
    /// <see cref="LifecycleDestinationPolicy.DestinationKey"/> scoped to its owner.
    /// <para>
    /// Held here rather than on <see cref="SubscriberQueue"/> precisely because a re-registration
    /// produces a <i>new</i> subscription id and therefore a new queue. Keying the quarantine on
    /// anything per-subscription would let an auto-retrying client shed it by re-registering, which
    /// is the failure this map exists to prevent.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quarantinedDestinations = new(
        StringComparer.Ordinal
    );
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RateLimitedReport _intakeDropReport;
    private readonly RateLimitedReport _queueDropReport;

    private Task? _pump;
    private long _intakeDrops;
    private long _queueDrops;
    private long _quarantines;
    private int _started;
    private int _stopped;
    private int _disposed;

    /// <summary>Creates the pipeline. Nothing runs until <see cref="StartAsync"/> is called.</summary>
    /// <param name="options">Delivery limits and timeouts. Validated here so a misconfiguration
    /// fails at construction rather than at the first delivery.</param>
    /// <param name="ownerResolver">The host's authority on who owns an event.</param>
    /// <param name="registry">Fan-out lookup for an owner's live subscriptions.</param>
    /// <param name="sender">Transport for a single attempt.</param>
    /// <param name="redactor">Applies the <c>lifecycle.content.full</c> gate per subscription.</param>
    /// <param name="timeProvider">Clock for retry backoff, deadlines, and the shutdown drain.</param>
    /// <param name="logger">Diagnostics sink. Receives identifiers and counts only.</param>
    public LifecycleDeliveryPipeline(
        LifecycleDeliveryOptions options,
        ILifecycleOwnerResolver ownerResolver,
        ILifecycleSubscriptionRegistry registry,
        ILifecycleDeliverySender sender,
        LifecycleContentRedactor redactor,
        TimeProvider timeProvider,
        ILogger<LifecycleDeliveryPipeline> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownerResolver);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _ownerResolver = ownerResolver;
        _registry = registry;
        _sender = sender;
        _redactor = redactor;
        _timeProvider = timeProvider;
        _logger = logger;
        _intakeDropReport = new RateLimitedReport(timeProvider, DropLogInterval);
        _queueDropReport = new RateLimitedReport(timeProvider, DropLogInterval);

        _intake = Channel.CreateBounded<LifecycleEventEnvelope>(
            new BoundedChannelOptions(options.IntakeQueueCapacity)
            {
                // Wait, combined with TryWrite, is what makes a full intake return false instead of
                // blocking. DropOldest/DropWrite would drop silently inside the channel, where the
                // pipeline could neither count the loss nor warn about it.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                // A subscriber's continuation must never run on the agent thread that published.
                AllowSynchronousContinuations = false,
            }
        );
    }

    /// <summary>Events discarded because the intake queue was full. Each is a gap in
    /// <c>source_sequence</c> for every subscriber that would have received it.</summary>
    public long IntakeDropCount => Interlocked.Read(ref _intakeDrops);

    /// <summary>Deliveries discarded because a subscriber's own queue was full. Each is a gap in
    /// that subscriber's <c>delivery_sequence</c>.</summary>
    public long QueueDropCount => Interlocked.Read(ref _queueDrops);

    /// <summary>Subscriptions retired by <see cref="LifecycleDeliveryOutcome.Gone"/> or by repeated
    /// failure.</summary>
    public long QuarantineCount => Interlocked.Read(ref _quarantines);

    /// <summary>
    /// Enqueues an event for delivery and returns. Never blocks, never throws, never waits on a
    /// subscriber.
    /// <para>
    /// The <paramref name="cancellationToken"/> is accepted for interface symmetry and deliberately
    /// not honored: the enqueue is a non-blocking channel write, so there is nothing to cancel, and
    /// honoring it would discard exactly the <c>run_completed</c>-with-cancelled events a subscriber
    /// most needs — the caller's token is usually already cancelled by the time they are published.
    /// </para>
    /// </summary>
    /// <param name="envelope">The event to deliver.</param>
    /// <param name="cancellationToken">Ignored; see the remarks above.</param>
    /// <returns>A completed task.</returns>
    public ValueTask PublishAsync(
        LifecycleEventEnvelope envelope,
        CancellationToken cancellationToken = default
    )
    {
        // Null and disabled are both silently ignored rather than thrown: this method is on the
        // agent's hot path and its contract is that a lifecycle problem never becomes a run failure.
        if (envelope is null || !_options.Enabled)
        {
            return ValueTask.CompletedTask;
        }

        if (!_intake.Writer.TryWrite(envelope))
        {
            ReportIntakeDrop(envelope);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Abandons everything this pipeline still holds for a revoked subscription: the backlog already
    /// queued for it, and any delivery currently working through its retry budget.
    /// </summary>
    /// <param name="owner">The owner the subscription belonged to. Checked, not trusted — an id
    /// alone must never reach across tenants (ADR 0005).</param>
    /// <param name="subscriptionId">The subscription that was revoked.</param>
    /// <remarks>
    /// <para>
    /// Unregistering a subscription is not enough on its own. The registry decides who is fanned out
    /// to <i>next</i>; the queue behind it still holds bodies that were serialized and signed while
    /// the subscription was live, and a worker may be sitting in a backoff about to send one. This is
    /// how a caller says "and stop the ones already in motion", which is what revoking is understood
    /// to mean.
    /// </para>
    /// <para>
    /// Silent when the subscription is unknown, already abandoned, or belongs to someone else: this
    /// is the tail end of a revocation whose authorization has already been decided, and reporting
    /// "no such subscription" back through it would turn the control plane into an oracle for which
    /// ids are real.
    /// </para>
    /// <para>
    /// The per-subscriber state is deliberately <b>kept</b> rather than removed. A fan-out that read
    /// the registry a moment before the revocation can still arrive here afterwards, and an absent
    /// entry would simply be recreated as a fresh, live queue. Leaving the abandoned one in place
    /// makes that arrival a no-op. Server-minted ids are never reused, so nothing legitimate is
    /// blocked, and the entry is released by the capacity sweep once the registry no longer knows it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subscriptionId"/> is null, empty, or
    /// whitespace.</exception>
    public void Abandon(LifecycleOwnerKey owner, string subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        if (
            !_subscribers.TryGetValue(subscriptionId, out var queue)
            || !string.Equals(
                queue.Subscription.Owner.Value,
                owner.Value,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        queue.Abandon();
    }

    /// <summary>
    /// Starts the background pump.
    /// <para>
    /// Implemented as <see cref="IHostedService"/> rather than as start-plus-<c>IAsyncDisposable</c>
    /// because only the hosted-service contract lets the host order this against everything else:
    /// stop runs while the process is still healthy, so the drain below has a working network to
    /// drain onto. Disposal happens after the container has already torn down, which is too late for
    /// a bounded drain to mean anything.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Unused; startup does no I/O.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Lifecycle delivery is disabled; no events will be dispatched");
            return Task.CompletedTask;
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _pump = Task.Run(() => PumpAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting events, gives in-flight deliveries
    /// <see cref="LifecycleDeliveryOptions.ShutdownDrainTimeout"/> to finish, then cancels whatever
    /// is left. The bound is unconditional: an unreachable subscriber must not be able to hold the
    /// process open.
    /// </summary>
    /// <param name="cancellationToken">The host's own stop budget, honored after the drain timeout
    /// has already forced cancellation.</param>
    /// <returns>A task that completes when the workers have stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        _ = _intake.Writer.TryComplete();

        var drain = DrainAsync();
        using var drainWindow = new CancellationTokenSource();
        var drainDeadline = Task.Delay(
            _options.ShutdownDrainTimeout,
            _timeProvider,
            drainWindow.Token
        );

        if (await Task.WhenAny(drain, drainDeadline) != drain)
        {
            _logger.LogWarning(
                "Lifecycle delivery drain exceeded {DrainTimeout}; cancelling in-flight deliveries for {SubscriberCount} subscribers",
                _options.ShutdownDrainTimeout,
                _subscribers.Count
            );
            await _shutdown.CancelAsync();
        }

        // Releases the drain timer whichever way the race went, so a won drain does not leave a
        // pending callback behind.
        await drainWindow.CancelAsync();
        await drain.WaitAsync(cancellationToken);
    }

    /// <summary>Releases the shutdown token source. Does not drain; call
    /// <see cref="StopAsync"/> for that.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Dispose();
    }

    private async Task DrainAsync()
    {
        if (_pump is { } pump)
        {
            await pump;
        }

        // Only safe after the pump has stopped: completing a writer the pump might still use would
        // turn ordinary shutdown into a burst of spurious "queue full" drops.
        var queues = _subscribers.Values.ToArray();
        foreach (var queue in queues)
        {
            queue.Complete();
        }

        await Task.WhenAll(queues.Select(queue => queue.Worker));
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var lifecycleEvent in _intake.Reader.ReadAllAsync(cancellationToken))
            {
                await DispatchAsync(lifecycleEvent, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown forced past its drain budget. Whatever is still queued is abandoned.
        }
        catch (Exception ex)
        {
            // The pump dying silently would look exactly like "no events are happening", which is the
            // hardest failure mode to notice from the outside.
            _logger.LogError(
                ex,
                "Lifecycle delivery pump stopped unexpectedly; no further events will be dispatched"
            );
        }
    }

    private async Task DispatchAsync(
        LifecycleEventEnvelope lifecycleEvent,
        CancellationToken cancellationToken
    )
    {
        LifecycleOwnerKey? owner;
        try
        {
            owner = await _ownerResolver.ResolveEventOwnerAsync(lifecycleEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // A resolver that throws has not said "nobody owns this" — it has failed to say anything.
            // Broadcasting on a non-answer is the single outcome ADR 0005 rules out, so the event is
            // dropped and the throw is logged as the defect it is.
            _logger.LogError(
                ex,
                "Lifecycle owner resolution faulted for event {EventId}; the event was dropped",
                lifecycleEvent.EventId
            );
            return;
        }

        if (owner is null)
        {
            return;
        }

        IReadOnlyList<LifecycleSubscription> subscriptions;
        try
        {
            subscriptions = _registry.ForOwner(owner);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Lifecycle subscription lookup faulted for event {EventId}; the event was dropped",
                lifecycleEvent.EventId
            );
            return;
        }

        foreach (var subscription in subscriptions)
        {
            if (!subscription.AcceptsEventType(lifecycleEvent.EventType))
            {
                continue;
            }

            FanOut(subscription, lifecycleEvent);
        }
    }

    private void FanOut(LifecycleSubscription subscription, LifecycleEventEnvelope lifecycleEvent)
    {
        var queue = GetOrCreateQueue(subscription);
        if (queue.IsAbandoned)
        {
            return;
        }

        // Re-authorized here, against the configuration in force now rather than the one that
        // admitted the subscription (ADR 0005). An operator who narrows the egress list to contain an
        // incident expects that to stop deliveries immediately, not once subscriptions happen to be
        // re-registered.
        if (!LifecycleDestinationPolicy.IsAuthorized(subscription.CallbackUri, _options))
        {
            // The sequence is still burned, for the same reason a queue-full drop burns one: the
            // subscriber's only way to learn it missed events is the gap. Suppressing the number here
            // would make an egress change look, from the far end, like nothing had happened.
            ReportDestinationRefused(subscription, queue.NextDeliverySequence());
            return;
        }

        string deliveryId;
        long sequence;
        byte[] body;
        try
        {
            var visible = _redactor.Redact(lifecycleEvent, subscription);
            deliveryId = Guid.NewGuid().ToString("n");

            // The sequence is claimed here, before the enqueue below is even attempted, and that is
            // intentional even though a failed enqueue then burns a number. Numbering after a
            // successful enqueue would renumber around the loss and hand the subscriber a contiguous
            // run that silently omits an event — the loss would become undetectable. Burning the
            // number makes it a gap, which is exactly the signal ADR 0002 asks for.
            sequence = queue.NextDeliverySequence();

            // Serialized once per delivery, never per attempt: a retry must re-send byte-identical
            // content under the same delivery id or the receiver's replay cache cannot recognize it.
            body = LifecycleSerializer.SerializeToUtf8Bytes(
                new LifecycleDeliveryEnvelope
                {
                    DeliveryId = deliveryId,
                    DeliverySequence = sequence,
                    Event = visible,
                }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Lifecycle event {EventId} could not be prepared for subscription {SubscriptionId}; the delivery was dropped",
                lifecycleEvent.EventId,
                subscription.SubscriptionId
            );
            return;
        }

        if (!queue.TryEnqueue(new PendingDelivery(deliveryId, sequence, body)))
        {
            ReportQueueDrop(subscription.SubscriptionId, sequence);
        }
    }

    private SubscriberQueue GetOrCreateQueue(LifecycleSubscription subscription)
    {
        if (_subscribers.TryGetValue(subscription.SubscriptionId, out var existing))
        {
            // Rotation replaces the signing secret in place on a new subscription instance; taking
            // the latest one keeps retries signing with the key the registry currently considers
            // current.
            existing.Refresh(subscription);
            return existing;
        }

        var queue = new SubscriberQueue(
            subscription,
            _options.MaxQueuedDeliveriesPerSubscriber,
            _options.MaxQueuedBytesPerSubscriber
        );

        // A brand-new subscription pointed at a destination still serving its cool-off starts
        // quarantined. This is the whole mechanism by which quarantine survives re-registration:
        // the id is new, the queue is new, but the endpoint that was failing is the same one.
        if (IsDestinationQuarantined(subscription))
        {
            queue.Abandon();
        }

        queue.Worker = Task.Run(
            () => RunSubscriberAsync(queue, _shutdown.Token),
            CancellationToken.None
        );
        _subscribers[subscription.SubscriptionId] = queue;

        if (_subscribers.Count > _options.MaxSubscriptions)
        {
            ReleaseUnregisteredSubscribers();
        }

        return queue;
    }

    /// <summary>
    /// Releases per-subscriber state for subscriptions the registry no longer knows.
    /// <para>
    /// Eviction is keyed on de-registration rather than on idleness, and that distinction is
    /// load-bearing: <c>delivery_sequence</c> must never restart for a subscription that has merely
    /// been quiet, because a restarted counter reads as a duplicate and destroys the subscriber's
    /// ability to detect gaps. A de-registered id, by contrast, is server-minted and never reused,
    /// so its state can be released with no such risk.
    /// </para>
    /// </summary>
    private void ReleaseUnregisteredSubscribers()
    {
        foreach (var entry in _subscribers)
        {
            var subscription = entry.Value.Subscription;
            bool stillRegistered;
            try
            {
                stillRegistered = _registry.TryGet(
                    subscription.Owner,
                    subscription.SubscriptionId,
                    out _
                );
            }
            catch (Exception ex)
            {
                // Retaining state costs memory; releasing it costs sequence continuity. On doubt,
                // keep.
                _logger.LogDebug(
                    ex,
                    "Lifecycle subscription {SubscriptionId} could not be re-checked; its state was retained",
                    subscription.SubscriptionId
                );
                continue;
            }

            if (stillRegistered || !_subscribers.TryRemove(entry.Key, out var removed))
            {
                continue;
            }

            // Abandoned, not completed: the subscription is gone, so flushing its backlog through
            // the sender would POST to an endpoint whose owner has already said it wants nothing
            // more. Sweeping at capacity must reach the same answer Abandon gives a caller, or a
            // revocation would mean two different things depending on how full the host happened to
            // be.
            removed.Abandon();
        }
    }

    private async Task RunSubscriberAsync(SubscriberQueue queue, CancellationToken shutdownToken)
    {
        try
        {
            await foreach (var pending in queue.Reader.ReadAllAsync(shutdownToken))
            {
                queue.OnDequeued(pending.Body.Length);
                if (queue.IsAbandoned)
                {
                    // Abandoning completes the writer, so this drains what was already queued and
                    // then ends the loop.
                    continue;
                }

                await DeliverAsync(queue, pending, shutdownToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown forced past its drain budget.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Lifecycle delivery worker for subscription {SubscriptionId} stopped unexpectedly",
                queue.Subscription.SubscriptionId
            );
        }
    }

    private async Task DeliverAsync(
        SubscriberQueue queue,
        PendingDelivery pending,
        CancellationToken shutdownToken
    )
    {
        var deadline = _timeProvider.GetUtcNow() + _options.DeliveryDeadline;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            // Re-checked before every attempt, because a revocation or a quarantine can land while
            // this delivery is mid-retry — sitting in a backoff, most likely — and the whole point of
            // both is that nothing further goes out. The attempt already in flight is not aborted:
            // it is one request that has already left, and cancelling it would buy nothing the next
            // attempt's refusal does not already buy.
            if (queue.IsAbandoned)
            {
                return;
            }

            // Re-checked before every attempt, not once per delivery. An operator narrowing the
            // allow-list to contain an incident expects the bleeding to stop now, and a delivery that
            // is already three attempts into a five-minute deadline is exactly the traffic they mean.
            // Abandoned rather than quarantined: the destination did nothing wrong, the configuration
            // moved, so widening the list again resumes deliveries without waiting out a cool-off.
            if (!LifecycleDestinationPolicy.IsAuthorized(queue.Subscription.CallbackUri, _options))
            {
                ReportDestinationRefused(queue.Subscription, pending.Sequence);
                return;
            }

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                OnDeliveryFailed(queue, pending, "deadline_exceeded", attempt - 1);
                return;
            }

            // The overall deadline is enforced by clamping each attempt to what is left of it, so a
            // long attempt can never overshoot the budget and no second timer is needed to notice.
            var attemptTimeout =
                _options.AttemptTimeout < remaining ? _options.AttemptTimeout : remaining;

            var result = await AttemptAsync(queue, pending, attemptTimeout, shutdownToken);
            if (result is null)
            {
                return;
            }

            switch (result.Outcome)
            {
                case LifecycleDeliveryOutcome.Succeeded:
                    queue.OnDeliverySucceeded();
                    return;

                case LifecycleDeliveryOutcome.Gone:
                    // Every other subscriber keeps receiving exactly as before: the only shared state
                    // this touches is the quarantine held against *this* destination, under this
                    // owner, which by construction no other subscriber is pointed at.
                    Quarantine(queue, "endpoint_gone");
                    return;

                case LifecycleDeliveryOutcome.Permanent:
                    OnDeliveryFailed(queue, pending, result.Reason, attempt);
                    return;

                case LifecycleDeliveryOutcome.Retryable:
                default:
                    if (attempt >= _options.MaxAttempts)
                    {
                        OnDeliveryFailed(queue, pending, "attempts_exhausted", attempt);
                        return;
                    }

                    var delay = ComputeRetryDelay(attempt, result.RetryAfter);
                    if (_timeProvider.GetUtcNow() + delay >= deadline)
                    {
                        OnDeliveryFailed(queue, pending, "deadline_exceeded", attempt);
                        return;
                    }

                    try
                    {
                        await Task.Delay(delay, _timeProvider, shutdownToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    private async Task<LifecycleDeliveryResult?> AttemptAsync(
        SubscriberQueue queue,
        PendingDelivery pending,
        TimeSpan attemptTimeout,
        CancellationToken shutdownToken
    )
    {
        using var attemptWindow = new CancellationTokenSource(attemptTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken,
            attemptWindow.Token
        );

        try
        {
            return await _sender.SendAsync(
                queue.Subscription,
                pending.DeliveryId,
                pending.Body,
                linked.Token
            );
        }
        catch (OperationCanceledException) when (!shutdownToken.IsCancellationRequested)
        {
            // Which token fired is the only thing separating "this attempt ran long" from "the host
            // is going away", and they call for opposite responses.
            return LifecycleDeliveryResult.Retryable("attempt_timeout");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A sender is contracted to classify its own failures, so reaching here is a defect in
            // the sender. It is still treated as retryable — the attempt cap bounds the damage — but
            // logged loudly, because a sender that throws will otherwise look like a flaky network.
            _logger.LogError(
                ex,
                "Lifecycle delivery sender threw for subscription {SubscriptionId}",
                queue.Subscription.SubscriptionId
            );
            return LifecycleDeliveryResult.Retryable("sender_fault");
        }
    }

    private TimeSpan ComputeRetryDelay(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } requested)
        {
            // Honored as a hint and clamped, never taken at face value. Unclamped, a single 429
            // carrying "retry after one hour" lets one subscriber park a worker — and stall every
            // delivery queued behind it — for an hour.
            var clamped = requested < TimeSpan.Zero ? TimeSpan.Zero : requested;
            return clamped > _options.MaxRetryAfter ? _options.MaxRetryAfter : clamped;
        }

        // Exponent capped before it is applied: with a large MaxAttempts, 2^n overflows TimeSpan
        // long before the cap below would ever be consulted.
        var growth = Math.Pow(2, Math.Min(attempt - 1, 16));
        var ceiling = Math.Min(
            _options.RetryBaseDelay.TotalMilliseconds * growth,
            _options.MaxRetryDelay.TotalMilliseconds
        );

        // Equal jitter: half the delay fixed so backoff still grows, half random so subscribers that
        // all failed against the same outage do not all retry against it at the same instant and
        // re-create the thundering herd the backoff exists to prevent.
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + (Random.Shared.NextDouble() * 0.5)));
    }

    private void OnDeliveryFailed(
        SubscriberQueue queue,
        PendingDelivery pending,
        string reason,
        int attempts
    )
    {
        var consecutive = queue.OnDeliveryFailed();
        _logger.LogWarning(
            "Lifecycle delivery {DeliverySequence} to subscription {SubscriptionId} abandoned after {Attempts} attempts ({Reason})",
            pending.Sequence,
            queue.Subscription.SubscriptionId,
            attempts,
            reason
        );

        if (consecutive >= QuarantineAfterConsecutiveFailedDeliveries)
        {
            Quarantine(queue, "consecutive_failures");
        }
    }

    private void Quarantine(SubscriberQueue queue, string reason)
    {
        queue.Abandon();
        _ = Interlocked.Increment(ref _quarantines);

        // The destination is held as well as the queue, so re-registering the same endpoint does not
        // hand it a fresh, unquarantined queue. Recorded even when the cool-off is zero: the write is
        // trivial and IsDestinationQuarantined treats an elapsed deadline as clear anyway.
        var destination = QuarantineKey(queue.Subscription);
        var until = _timeProvider.GetUtcNow() + _options.QuarantineCooloff;

        // Extend only. Two subscriptions failing against one endpoint must not let the second one's
        // quarantine shorten the first one's.
        _ = _quarantinedDestinations.AddOrUpdate(
            destination,
            until,
            (_, existing) => existing > until ? existing : until
        );

        _logger.LogWarning(
            "Lifecycle subscription {SubscriptionId} quarantined ({Reason}); queued deliveries are dropped and no further deliveries will be attempted. The destination stays quarantined for {Cooloff}, including for subscriptions registered in the meantime.",
            queue.Subscription.SubscriptionId,
            reason,
            _options.QuarantineCooloff
        );
    }

    /// <summary>
    /// Whether this subscription's destination is inside an unexpired quarantine window.
    /// </summary>
    private bool IsDestinationQuarantined(LifecycleSubscription subscription)
    {
        var destination = QuarantineKey(subscription);
        if (!_quarantinedDestinations.TryGetValue(destination, out var until))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() < until)
        {
            return true;
        }

        // Swept on read rather than by a timer. The map is bounded by the number of distinct
        // destinations that have ever failed, and a lapsed entry is only interesting to the caller
        // that just asked about it. Removing the exact value read avoids racing an extension.
        _ = _quarantinedDestinations.TryRemove(new KeyValuePair<string, DateTimeOffset>(destination, until));
        return false;
    }

    /// <summary>
    /// Owner-scoped destination identity. Owner-scoped because one tenant's dead endpoint must not
    /// silence another tenant that happens to publish to the same host.
    /// </summary>
    private static string QuarantineKey(LifecycleSubscription subscription) =>
        // Joined by a unit separator rather than a printable delimiter: the owner half is an
        // app-supplied identity string, so any delimiter it could itself contain would let one owner
        // name a key that collides with another owner's destination.
        subscription.Owner.Value
        + "\u001f"
        + LifecycleDestinationPolicy.DestinationKey(subscription.CallbackUri);

    private void ReportDestinationRefused(LifecycleSubscription subscription, long sequence)
    {
        var total = Interlocked.Increment(ref _queueDrops);

        // Rate-limited alongside queue-full drops: once the allow-list stops admitting a destination,
        // every subsequent event for it is refused too, so this is a burst by construction.
        if (!_queueDropReport.ShouldReport(out var suppressed))
        {
            return;
        }

        _logger.LogWarning(
            "Lifecycle delivery {DeliverySequence} to subscription {SubscriptionId} was dropped: callback host {CallbackHost} is not an allowed callback host under the current configuration. {SuppressedCount} further drops were suppressed, {TotalDropCount} total. The subscriber will observe a gap in delivery_sequence.",
            sequence,
            subscription.SubscriptionId,
            subscription.CallbackUri.Host,
            suppressed,
            total
        );
    }

    private void ReportIntakeDrop(LifecycleEventEnvelope lifecycleEvent)
    {
        var total = Interlocked.Increment(ref _intakeDrops);
        if (!_intakeDropReport.ShouldReport(out var suppressed))
        {
            return;
        }

        _logger.LogWarning(
            "Lifecycle intake is full; dropped event {EventId} of type {EventType}. {SuppressedCount} further drops were suppressed, {TotalDropCount} total. Subscribers will observe a gap in source_sequence.",
            lifecycleEvent.EventId,
            lifecycleEvent.EventType,
            suppressed,
            total
        );
    }

    private void ReportQueueDrop(string subscriptionId, long sequence)
    {
        var total = Interlocked.Increment(ref _queueDrops);
        if (!_queueDropReport.ShouldReport(out var suppressed))
        {
            return;
        }

        _logger.LogWarning(
            "Lifecycle queue for subscription {SubscriptionId} is full; dropped delivery {DeliverySequence}. {SuppressedCount} further drops were suppressed, {TotalDropCount} total. The subscriber will observe a gap in delivery_sequence.",
            subscriptionId,
            sequence,
            suppressed,
            total
        );
    }

    /// <summary>One serialized delivery, waiting for its subscriber's worker.</summary>
    private readonly record struct PendingDelivery(string DeliveryId, long Sequence, byte[] Body);

    /// <summary>
    /// One subscriber's bulkhead: its queue, its sequence counter, its failure streak, and its
    /// worker. Nothing here is shared with another subscriber, which is what makes a quarantine or a
    /// backlog local to the endpoint that caused it.
    /// </summary>
    private sealed class SubscriberQueue
    {
        private readonly Channel<PendingDelivery> _queue;
        private readonly long _maxQueuedBytes;

        private LifecycleSubscription _subscription;
        private long _queuedBytes;
        private long _deliverySequence;
        private int _consecutiveFailures;
        private volatile bool _abandoned;

        internal SubscriberQueue(
            LifecycleSubscription subscription,
            int maxQueuedDeliveries,
            long maxQueuedBytes
        )
        {
            _subscription = subscription;
            _maxQueuedBytes = maxQueuedBytes;
            _queue = Channel.CreateBounded<PendingDelivery>(
                new BoundedChannelOptions(maxQueuedDeliveries)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                }
            );
        }

        internal LifecycleSubscription Subscription => Volatile.Read(ref _subscription);

        internal ChannelReader<PendingDelivery> Reader => _queue.Reader;

        /// <summary>
        /// Whether this queue has stopped delivering for good — quarantined for failing, or
        /// abandoned because its subscription was revoked. One flag for both because from the
        /// queue's side they are the same state: nothing more goes out, and no re-registration can
        /// revive this instance.
        /// </summary>
        internal bool IsAbandoned => _abandoned;

        internal Task Worker { get; set; } = Task.CompletedTask;

        internal void Refresh(LifecycleSubscription subscription) =>
            Volatile.Write(ref _subscription, subscription);

        internal long NextDeliverySequence() => Interlocked.Increment(ref _deliverySequence);

        /// <summary>
        /// Enqueues if both budgets allow. Two limits, not one, because a count limit is not a memory
        /// limit: 256 queued deliveries is a bounded number of objects but an unbounded number of
        /// bytes, and a large event is exactly the kind that backs up.
        /// </summary>
        internal bool TryEnqueue(PendingDelivery delivery)
        {
            // Reserved before the write, so the worker can never subtract bytes that were never added
            // and drive the accounting negative.
            var reserved = Interlocked.Add(ref _queuedBytes, delivery.Body.Length);
            if (reserved > _maxQueuedBytes || !_queue.Writer.TryWrite(delivery))
            {
                _ = Interlocked.Add(ref _queuedBytes, -delivery.Body.Length);
                return false;
            }

            return true;
        }

        internal void OnDequeued(int byteCount) => Interlocked.Add(ref _queuedBytes, -byteCount);

        internal void OnDeliverySucceeded() => Interlocked.Exchange(ref _consecutiveFailures, 0);

        internal int OnDeliveryFailed() => Interlocked.Increment(ref _consecutiveFailures);

        internal void Complete() => _ = _queue.Writer.TryComplete();

        /// <summary>
        /// Stops this queue delivering, now and for the rest of its life.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Complete"/>, which drains the backlog <i>through</i> the sender:
        /// that is what an orderly shutdown wants and exactly what a quarantine or a revocation must
        /// not do. Completing the writer as well lets the worker walk the backlog — skipping each
        /// item — and then exit, rather than parking forever on a queue nothing will ever read.
        /// </remarks>
        internal void Abandon()
        {
            _abandoned = true;
            _ = _queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Lets at most one report through per interval and counts what it suppressed, so a burst costs
    /// one log line plus a number rather than one line per event.
    /// </summary>
    private sealed class RateLimitedReport(TimeProvider timeProvider, TimeSpan interval)
    {
        private readonly TimeProvider _timeProvider = timeProvider;
        private readonly long _intervalTicks = interval.Ticks;

        private long _nextReportTicks;
        private long _suppressed;

        internal bool ShouldReport(out long suppressedSinceLastReport)
        {
            var now = _timeProvider.GetUtcNow().UtcTicks;
            var next = Interlocked.Read(ref _nextReportTicks);

            if (
                now < next
                || Interlocked.CompareExchange(ref _nextReportTicks, now + _intervalTicks, next)
                    != next
            )
            {
                _ = Interlocked.Increment(ref _suppressed);
                suppressedSinceLastReport = 0;
                return false;
            }

            suppressedSinceLastReport = Interlocked.Exchange(ref _suppressed, 0);
            return true;
        }
    }
}
