using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Approval;

/// <summary>
/// Splits a tool invocation into a <b>prepare</b> phase — policy checks and, if configured, an
/// awaited approval decision — and an <b>invoke</b> phase that runs the handler on exactly the
/// arguments that were approved.
/// </summary>
/// <remarks>
/// <para>
/// The split exists so a batch of tool calls can have several approval decisions in flight at once
/// while still executing in the caller's original order. Wrapping handlers instead would have been
/// a smaller change, but a wrapper sits <i>inside</i> the invocation and so cannot express "prepare
/// everything, then invoke in order" — it would serialize approvals behind execution order.
/// </para>
/// <para>
/// <b>Approval is fail-closed and unanimous-allow.</b> A handler runs only after every configured
/// approver explicitly allowed the call. Denial, timeout, overload, a missing approver, an approver
/// that throws, revocation, and cancellation all block it. The observable invariant is that a
/// handler runs exactly zero or one times — never twice, and never after a refusal.
/// </para>
/// <para>
/// With nothing configured, <see cref="IsEnabled"/> is <see langword="false"/>, every call is
/// approved without consulting anything, and no hash is computed. Behaviour is then identical to
/// having no approval layer at all.
/// </para>
/// </remarks>
public sealed class ToolInvocationPreparer
{
    private readonly ToolApprovalOptions _options;
    private readonly ILogger _logger;
    private int _pendingApprovals;

    /// <summary>
    /// Creates a preparer.
    /// </summary>
    /// <param name="options">
    /// What to enforce. Null — the default — configures nothing, leaving the preparer inert.
    /// </param>
    /// <param name="logger">Optional logger for refusals and approver failures.</param>
    public ToolInvocationPreparer(ToolApprovalOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new ToolApprovalOptions();
        _logger = logger ?? NullLogger.Instance;
        RequiresApprovalDecision = _options.RequireApproval || _options.Gates.Count > 0;
        IsEnabled = RequiresApprovalDecision || _options.ProviderPolicy != null || _options.HostPolicy != null;
    }

    /// <summary>A preparer that enforces nothing. Use where a call site has no host configuration.</summary>
    public static ToolInvocationPreparer Disabled { get; } = new();

    /// <summary>
    /// Whether anything is configured. When <see langword="false"/>, <see cref="PrepareAsync"/>
    /// approves every call synchronously and consults nothing.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>Whether an approval decision is required (a gate is configured, or demanded).</summary>
    private bool RequiresApprovalDecision { get; }

    /// <summary>
    /// Settles whether <paramref name="request"/> may run, freezing its arguments first so that
    /// what is decided and what executes cannot diverge.
    /// </summary>
    /// <param name="request">The call to decide.</param>
    /// <param name="cancellationToken">Cancelled when the run or turn is cancelled.</param>
    /// <returns>
    /// The settled decision. This method does not throw on refusal or on cancellation during an
    /// approval wait — both are reported as a blocking outcome, so a caller can turn them into an
    /// ordinary error result rather than unwinding a batch.
    /// </returns>
    public async Task<PreparedToolInvocation> PrepareAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var arguments = CanonicalToolArguments.Freeze(request.ArgumentsJson);

        if (!IsEnabled)
        {
            return Settle(request, arguments, ToolApprovalVerdict.Allow());
        }

        // The expiry is fixed once, here, so every policy and every approver sees the same
        // deadline and none of them can extend it by taking longer to answer.
        var expiresAt = ComputeExpiry(request.OperationDeadline);
        var context = new ToolApprovalContext
        {
            ToolName = request.ToolName,
            ToolCallId = request.ToolCallId,
            Arguments = arguments,
            ExecutionTarget = request.ExecutionTarget,
            ThreadId = request.ThreadId,
            RunId = request.RunId,
            GenerationId = request.GenerationId,
            ExpiresAt = expiresAt,
        };

        // Precedence: provider policy, then host policy, then approvers. Neither policy opens a
        // gate, so a call a local rule already refuses never reaches a human or a remote service.
        var providerVerdict = await EvaluatePolicyAsync(
                _options.ProviderPolicy,
                context,
                ToolApprovalOutcomes.ProviderPolicyDenied,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!providerVerdict.IsAllowed)
        {
            return Settle(request, arguments, providerVerdict);
        }

        var hostVerdict = await EvaluatePolicyAsync(
                _options.HostPolicy,
                context,
                ToolApprovalOutcomes.HostPolicyDenied,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!hostVerdict.IsAllowed)
        {
            return Settle(request, arguments, hostVerdict);
        }

        if (!RequiresApprovalDecision)
        {
            return Settle(request, arguments, ToolApprovalVerdict.Allow());
        }

        var approval = await RequestApprovalAsync(context, expiresAt, cancellationToken).ConfigureAwait(false);
        return Settle(request, arguments, approval);
    }

    /// <summary>
    /// Runs <paramref name="handler"/> if — and only if — <paramref name="prepared"/> was approved,
    /// passing it the frozen arguments rather than anything the caller still holds.
    /// </summary>
    /// <param name="prepared">The settled decision from <see cref="PrepareAsync"/>.</param>
    /// <param name="handler">The handler to run when the call was allowed.</param>
    /// <param name="context">Per-invocation metadata for the handler.</param>
    /// <param name="cancellationToken">Passed through to the handler.</param>
    /// <returns>The handler's result, or the refusal rendered as an error result.</returns>
    public Task<ToolCallResult> InvokeAsync(
        PreparedToolInvocation prepared,
        ToolCallResultHandler handler,
        ToolCallContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(handler);

        if (!prepared.IsApproved)
        {
            _logger.LogWarning(
                "Tool call blocked before execution: ToolName={ToolName}, ToolCallId={ToolCallId}, "
                    + "Outcome={Outcome}, ArgumentsHash={ArgumentsHash}",
                prepared.ToolName,
                prepared.ToolCallId,
                prepared.Outcome,
                prepared.Arguments.Sha256Hex
            );

            return Task.FromResult(prepared.ToBlockedResult());
        }

        // The frozen text, never the caller's copy — an approver decided on these exact bytes.
        return handler(prepared.Arguments.Json, context, cancellationToken);
    }

    private static PreparedToolInvocation Settle(
        ToolInvocationRequest request,
        CanonicalToolArguments arguments,
        ToolApprovalVerdict verdict
    ) =>
        new()
        {
            ToolName = request.ToolName,
            ToolCallId = request.ToolCallId,
            Arguments = arguments,
            ExecutionTarget = request.ExecutionTarget,
            Outcome = verdict.Outcome ?? ToolApprovalOutcomes.Denied,
            Reason = verdict.Reason,
        };

    private DateTimeOffset ComputeExpiry(DateTimeOffset? operationDeadline)
    {
        var ceiling = _options.TimeProvider.GetUtcNow() + _options.MaxApprovalWait;
        return operationDeadline is { } deadline && deadline < ceiling ? deadline : ceiling;
    }

    private async ValueTask<ToolApprovalVerdict> EvaluatePolicyAsync(
        IToolExecutionPolicy? policy,
        ToolApprovalContext context,
        string denialOutcome,
        CancellationToken cancellationToken
    )
    {
        if (policy == null)
        {
            return ToolApprovalVerdict.Allow();
        }

        try
        {
            var verdict = await policy.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            if (verdict.IsAllowed)
            {
                return verdict;
            }

            // A plain refusal is attributed to the slot the policy occupies, so `Deny()` — the
            // obvious thing to write — reports as provider_policy_denied or host_policy_denied
            // rather than as the generic code. A policy that names something more specific keeps it.
            return
                string.IsNullOrEmpty(verdict.Outcome)
                || string.Equals(verdict.Outcome, ToolApprovalOutcomes.Denied, StringComparison.Ordinal)
                ? ToolApprovalVerdict.Blocked(denialOutcome, verdict.Reason)
                : verdict;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Tool execution policy threw and therefore blocks: ToolName={ToolName}, ToolCallId={ToolCallId}",
                context.ToolName,
                context.ToolCallId
            );
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.HookError, ex.Message);
        }
    }

    private async Task<ToolApprovalVerdict> RequestApprovalAsync(
        ToolApprovalContext context,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken
    )
    {
        var gates = _options.Gates;
        if (gates.Count == 0 || gates.Any(gate => gate == null))
        {
            // Demanding approval with nothing able to grant it is a misconfiguration, and the
            // fail-closed reading of a misconfiguration is "no".
            _logger.LogError(
                "Tool call requires approval but no approver is configured: ToolName={ToolName}, ToolCallId={ToolCallId}",
                context.ToolName,
                context.ToolCallId
            );
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.MissingApprover);
        }

        if (Interlocked.Increment(ref _pendingApprovals) > _options.MaxPendingApprovals)
        {
            _ = Interlocked.Decrement(ref _pendingApprovals);
            _logger.LogWarning(
                "Tool call refused because too many approvals are pending: ToolName={ToolName}, Limit={Limit}",
                context.ToolName,
                _options.MaxPendingApprovals
            );
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Overload);
        }

        try
        {
            var remaining = expiresAt - _options.TimeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Timeout);
            }

            using var expiry = new CancellationTokenSource(remaining, _options.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);

            var verdict = await CollectUnanimousAllowAsync(context, gates, linked.Token).ConfigureAwait(false);

            // The token wins ties. A decision that arrives as the run is cancelled or the deadline
            // passes is a decision nobody is waiting for any more, and acting on it would execute a
            // handler after the caller believed the call was abandoned.
            if (cancellationToken.IsCancellationRequested)
            {
                return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Cancelled);
            }

            if (expiry.IsCancellationRequested)
            {
                return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Timeout);
            }

            return verdict;
        }
        finally
        {
            _ = Interlocked.Decrement(ref _pendingApprovals);
        }
    }

    private async Task<ToolApprovalVerdict> CollectUnanimousAllowAsync(
        ToolApprovalContext context,
        IReadOnlyList<IToolApprovalGate> gates,
        CancellationToken cancellationToken
    )
    {
        // One approver's refusal is enough, so stop the others as soon as it lands rather than
        // making the caller wait out the slowest approver for an answer that cannot change.
        using var shortCircuit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pending = new List<Task<ToolApprovalVerdict>>(gates.Count);
        foreach (var gate in gates)
        {
            pending.Add(RunGateAsync(gate, context, shortCircuit.Token));
        }

        var verdict = ToolApprovalVerdict.Allow();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            _ = pending.Remove(completed);

            var gateVerdict = await completed.ConfigureAwait(false);
            if (gateVerdict.IsAllowed)
            {
                continue;
            }

            verdict = gateVerdict;
            await shortCircuit.CancelAsync().ConfigureAwait(false);
            break;
        }

        return verdict;
    }

    private async Task<ToolApprovalVerdict> RunGateAsync(
        IToolApprovalGate gate,
        ToolApprovalContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var verdict = await gate.RequestApprovalAsync(context, cancellationToken).ConfigureAwait(false);

            // A gate that returns `default` has not allowed anything — the null outcome is read as
            // a denial rather than as an omission.
            return verdict.IsAllowed || !string.IsNullOrEmpty(verdict.Outcome)
                ? verdict
                : ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Denied, verdict.Reason);
        }
        catch (OperationCanceledException)
        {
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.Cancelled);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Tool approval gate threw and therefore blocks: Gate={Gate}, ToolName={ToolName}, ToolCallId={ToolCallId}",
                gate.GetType().Name,
                context.ToolName,
                context.ToolCallId
            );
            return ToolApprovalVerdict.Blocked(ToolApprovalOutcomes.HookError, ex.Message);
        }
    }
}
