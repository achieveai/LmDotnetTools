using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using CoreOutcomes = AchieveAi.LmDotnetTools.LmCore.Approval.ToolApprovalOutcomes;
using WireOutcomes = AchieveAi.LmDotnetTools.LmLifecycle.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// Delivers an approval request to one subscriber's callback.
/// </summary>
/// <remarks>
/// A seam, not a pipeline. The gate's job is to decide who may be asked and what they may be shown;
/// signing and posting the callback belongs to the lifecycle delivery runtime, which is wired
/// separately. Keeping the two apart means the gate can be tested — and the "who may see the
/// arguments" rule verified — without an HTTP stack.
/// </remarks>
public interface IToolApprovalRequestPublisher
{
    /// <summary>Sends one approval request to one subscriber.</summary>
    /// <param name="subscriber">The approver to ask. Already checked for
    /// <see cref="LifecycleCapabilities.ToolApprovalDecide"/> and already owner-scoped.</param>
    /// <param name="request">The request, tailored to what this subscriber is allowed to see.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>A task that completes when the request has been handed to the delivery runtime.</returns>
    ValueTask PublishAsync(
        LifecycleSubscription subscriber,
        ToolApprovalRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// An <see cref="IToolApprovalGate"/> that asks a remote subscriber whether a tool call may run
/// (ADR 0003 + ADR 0005).
/// </summary>
/// <remarks>
/// <para>
/// Every path that is not an explicit remote allow returns a blocking verdict. There is no
/// configuration, no error, and no timeout that makes this gate answer <c>Allow</c> on its own: an
/// unresolvable owner, an owner with no capable approver, a full store, a delivery failure, an
/// expired wait and a thrown exception all block, each with the outcome code that names what
/// actually happened so an operator can tell a denial from a defect.
/// </para>
/// <para>
/// The gate is owner-scoped before it is capability-scoped. Resolving the thread's owner first means
/// an approver is only ever asked about calls inside its own tenancy, and a thread whose owner the
/// host cannot name is refused rather than offered to whoever happens to be subscribed.
/// </para>
/// </remarks>
public sealed class RemoteToolApprovalGate : IToolApprovalGate
{
    private readonly RemoteApprovalOptions _options;
    private readonly RemoteApprovalStore _store;
    private readonly ILifecycleOwnerResolver _ownerResolver;
    private readonly ILifecycleSubscriptionRegistry _subscriptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoteToolApprovalGate> _logger;
    private readonly IToolApprovalRequestPublisher? _publisher;

    /// <summary>Creates the gate.</summary>
    /// <param name="options">Remote-approval configuration.</param>
    /// <param name="store">Holds the pending request and receives the decision.</param>
    /// <param name="ownerResolver">The only accepted source of a <see cref="LifecycleOwnerKey"/>.</param>
    /// <param name="subscriptions">Fan-out lookup for the owner's approvers.</param>
    /// <param name="timeProvider">Separates "the wait expired" from "the run was cancelled".</param>
    /// <param name="logger">Diagnostics sink. Never receives tool arguments.</param>
    /// <param name="publisher">
    /// Optional delivery seam. When absent the request is registered but never sent, so the wait
    /// simply expires and the call is blocked — dormant, and dormant in the fail-closed direction.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public RemoteToolApprovalGate(
        RemoteApprovalOptions options,
        RemoteApprovalStore store,
        ILifecycleOwnerResolver ownerResolver,
        ILifecycleSubscriptionRegistry subscriptions,
        TimeProvider timeProvider,
        ILogger<RemoteToolApprovalGate> logger,
        IToolApprovalRequestPublisher? publisher = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ownerResolver);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _store = store;
        _ownerResolver = ownerResolver;
        _subscriptions = subscriptions;
        _timeProvider = timeProvider;
        _logger = logger;
        _publisher = publisher;
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalVerdict> RequestApprovalAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        // A gate in the list is a gate that gates. `Enabled` decides whether the host constructs and
        // registers this gate at all; reaching here with it off is a wiring mistake, and the safe
        // reading of "remote approval is disabled" is "there is no remote approver", not "allow".
        if (!_options.Enabled)
        {
            _logger.LogError(
                "Remote tool approval is disabled but the gate was consulted for tool {Tool}; blocking.",
                context.ToolName
            );
            return ToolApprovalVerdict.Blocked(
                CoreOutcomes.MissingApprover,
                "remote tool approval is disabled"
            );
        }

        RemoteApprovalTicket? ticket = null;
        try
        {
            var owner = await _ownerResolver
                .ResolveThreadOwnerAsync(context.ThreadId, cancellationToken)
                .ConfigureAwait(false);
            if (owner is null)
            {
                _logger.LogWarning(
                    "Blocking tool {Tool}: no owner resolved for the calling thread, so no approver is entitled to answer.",
                    context.ToolName
                );
                return ToolApprovalVerdict.Blocked(
                    CoreOutcomes.MissingApprover,
                    "the calling thread has no resolvable owner"
                );
            }

            var approvers = CapableApprovers(owner);
            if (approvers.Count == 0)
            {
                _logger.LogWarning(
                    "Blocking tool {Tool}: owner {Owner} has no subscription holding {Capability}.",
                    context.ToolName,
                    owner.Value,
                    LifecycleCapabilities.ToolApprovalDecide
                );
                return ToolApprovalVerdict.Blocked(
                    CoreOutcomes.MissingApprover,
                    "no subscriber may decide tool approvals for this owner"
                );
            }

            ticket = _store.TryRegister(owner, context);
            if (ticket is null)
            {
                return ToolApprovalVerdict.Blocked(
                    CoreOutcomes.Overload,
                    "too many approvals are already pending"
                );
            }

            if (!await TryPublishAsync(ticket.Request, approvers, context, cancellationToken)
                .ConfigureAwait(false))
            {
                return ToolApprovalVerdict.Blocked(
                    CoreOutcomes.HookError,
                    "the approval request could not be delivered to any approver"
                );
            }

            // The supplied token already carries both the run's cancellation and the effective
            // expiry (see ToolInvocationPreparer), so there is no second timer to get out of step.
            var decision = await ticket
                .Decision.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return WireOutcomes.IsAllowed(decision.Decision)
                ? ToolApprovalVerdict.Allow()
                : ToolApprovalVerdict.Blocked(CoreOutcomes.Denied, decision.Reason);
        }
        catch (OperationCanceledException)
        {
            // Expiry and cancellation arrive on the same token, so the clock is what tells them
            // apart — and the distinction is worth keeping: "nobody answered in time" and "the run
            // was abandoned" call for different operator responses.
            var expired = _timeProvider.GetUtcNow() >= context.ExpiresAt;
            return ToolApprovalVerdict.Blocked(
                expired ? CoreOutcomes.Timeout : CoreOutcomes.Cancelled,
                expired ? "no approver answered before the request expired" : "the run was cancelled"
            );
        }
#pragma warning disable CA1031 // A gate must convert every fault into a blocking verdict, not propagate it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Remote approval failed for tool {Tool}; blocking.", context.ToolName);
            return ToolApprovalVerdict.Blocked(CoreOutcomes.HookError, "remote approval faulted");
        }
        finally
        {
            // Runs on every exit, including cancellation, so a pending entry never outlives the wait
            // that created it and cannot consume an admission slot forever.
            ticket?.Dispose();
        }
    }

    /// <summary>The owner's subscriptions that are actually allowed to answer an approval.</summary>
    private IReadOnlyList<LifecycleSubscription> CapableApprovers(LifecycleOwnerKey owner) =>
        [
            .. _subscriptions
                .ForOwner(owner)
                .Where(s => s.HasCapability(LifecycleCapabilities.ToolApprovalDecide)),
        ];

    /// <summary>
    /// Offers the request to every capable approver.
    /// </summary>
    /// <returns>
    /// <c>true</c> when at least one approver was reached. A publisher that throws for every
    /// subscriber means nobody can answer, so the call is blocked immediately rather than made to
    /// wait out an expiry nothing will ever satisfy.
    /// </returns>
    private async ValueTask<bool> TryPublishAsync(
        ToolApprovalRequest request,
        IReadOnlyList<LifecycleSubscription> approvers,
        ToolApprovalContext context,
        CancellationToken cancellationToken
    )
    {
        if (_publisher is null)
        {
            // No delivery wired yet: the request stands, unanswered, and the wait expires. Reporting
            // success here keeps that path a timeout rather than a spurious delivery error.
            return true;
        }

        var delivered = false;
        foreach (var approver in approvers)
        {
            try
            {
                await _publisher
                    .PublishAsync(approver, ForSubscriber(request, approver, context), cancellationToken)
                    .ConfigureAwait(false);
                delivered = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One unreachable approver must not stop the others being asked.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(
                    ex,
                    "Could not deliver approval {RequestId} to subscription {SubscriptionId}.",
                    request.RequestId,
                    approver.SubscriptionId
                );
            }
        }

        return delivered;
    }

    /// <summary>
    /// Tailors the request to one subscriber: the argument text is included only for an approver
    /// granted <see cref="LifecycleCapabilities.ContentFull"/>, leaving everyone else the hash as the
    /// sole description of what will run. A fresh instance per subscriber because
    /// <see cref="ToolApprovalRequest"/> is mutable — sharing one would let a publisher's edit for a
    /// privileged approver become what an unprivileged one receives.
    /// </summary>
    private static ToolApprovalRequest ForSubscriber(
        ToolApprovalRequest request,
        LifecycleSubscription subscriber,
        ToolApprovalContext context
    ) =>
        request with
        {
            Arguments = subscriber.HasCapability(LifecycleCapabilities.ContentFull)
                ? context.Arguments.Json
                : null,
        };
}
