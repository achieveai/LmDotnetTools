using System.Text.Json;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Posts a <see cref="ToolApprovalRequest"/> to one approver's callback over the same signed
/// transport lifecycle events use (ADR 0003 + ADR 0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>Targeted, not fanned out.</b> An approval request is addressed to a subscription that
/// <see cref="RemoteToolApprovalGate"/> has already checked for
/// <see cref="LifecycleCapabilities.ToolApprovalDecide"/>, and it is tailored per subscriber — the
/// argument text is present only for an approver that may see it. Routing it through
/// <see cref="LifecycleDeliveryPipeline.PublishAsync"/> would fan it out to every subscription of
/// the owner whose event filter happened to accept it, which is a different and much wider audience
/// than the one the gate authorized. So this sends directly, through
/// <see cref="ILifecycleDeliverySender"/>, which is the piece that signs.
/// </para>
/// <para>
/// <b>One attempt, and a failure is an exception.</b> The gate's contract is that a throw means "this
/// approver was not reached" — it catches, logs, and moves on to the next approver, blocking the call
/// only when <i>every</i> approver failed. Retrying here would be retrying inside that loop, delaying
/// the approvers that are still healthy, and an approval request carries its own expiry, so a retry
/// that outlives it accomplishes nothing. The pipeline's retry budget exists because a lost event is
/// unrecoverable; a lost approval request is not — it blocks, which is the safe direction.
/// </para>
/// <para>
/// <b>The destination is re-authorized here.</b> This is the third of the three moments ADR 0005
/// requires (registration, enqueue, every attempt). The subscription was admitted under whatever
/// allow-list was configured then; an operator who has since narrowed
/// <see cref="LifecycleDeliveryOptions.AllowedCallbackHosts"/> around an incident must not still have
/// tool arguments posted to the host they removed.
/// </para>
/// </remarks>
public sealed class LifecycleApprovalRequestPublisher : IToolApprovalRequestPublisher
{
    private readonly ILifecycleDeliverySender _sender;
    private readonly LifecycleDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LifecycleApprovalRequestPublisher> _logger;

    /// <summary>Creates the publisher.</summary>
    /// <param name="sender">Signs and posts one attempt. Shared with the delivery pipeline.</param>
    /// <param name="options">Supplies the attempt timeout and the egress allow-list.</param>
    /// <param name="timeProvider">Times the attempt window.</param>
    /// <param name="logger">Diagnostics sink. Receives identifiers only — never arguments.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public LifecycleApprovalRequestPublisher(
        ILifecycleDeliverySender sender,
        LifecycleDeliveryOptions options,
        TimeProvider timeProvider,
        ILogger<LifecycleApprovalRequestPublisher> logger
    )
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sender = sender;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The destination is no longer authorized, or the subscriber did not accept the request. Both
    /// mean "this approver was not asked", which is what the gate needs to know.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled — the run itself is going away. An attempt
    /// that merely ran out of its own timeout does <i>not</i> surface as cancellation; see the remarks
    /// on the timeout below.
    /// </exception>
    public async ValueTask PublishAsync(
        LifecycleSubscription subscriber,
        ToolApprovalRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(request);

        if (!LifecycleDestinationPolicy.IsAuthorized(subscriber.CallbackUri, _options))
        {
            throw new InvalidOperationException(
                $"Subscription {subscriber.SubscriptionId} points at a callback destination that is no "
                    + "longer authorized by the configured egress policy."
            );
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(request, LifecycleSerializer.Options);

        // Distinct from the request id: the request id is what the approver answers about and is
        // stable across every approver, while the delivery id identifies this one POST and is what
        // the receiver's replay cache keys on. Sharing them would make two approvers' deliveries
        // look like a replay of each other.
        var deliveryId = Guid.NewGuid().ToString("n");

        // Timed off the injected clock, not the system one, so the attempt window is the same
        // testable construct the delivery pipeline uses for its own attempts.
        using var attemptWindow = new CancellationTokenSource(_options.AttemptTimeout, _timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptWindow.Token);

        LifecycleDeliveryResult result;
        try
        {
            result = await _sender.SendAsync(subscriber, deliveryId, body, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt timeout fired, not the run. Converted deliberately: the gate rethrows
            // OperationCanceledException, so letting this escape would abandon the approval for every
            // remaining approver and report the run as cancelled — a slow endpoint would masquerade
            // as an abandoned run.
            _logger.LogWarning(
                "Approval request {RequestId} to subscription {SubscriptionId} timed out after {Timeout}.",
                request.RequestId,
                subscriber.SubscriptionId,
                _options.AttemptTimeout
            );
            throw new InvalidOperationException($"The approval request timed out after {_options.AttemptTimeout}.");
        }

        if (result.Outcome != LifecycleDeliveryOutcome.Succeeded)
        {
            throw new InvalidOperationException(
                $"The approver rejected the approval request ({result.Outcome}: {result.Reason})."
            );
        }

        _logger.LogDebug(
            "Approval request {RequestId} delivered to subscription {SubscriptionId} as {DeliveryId}.",
            request.RequestId,
            subscriber.SubscriptionId,
            deliveryId
        );
    }
}
