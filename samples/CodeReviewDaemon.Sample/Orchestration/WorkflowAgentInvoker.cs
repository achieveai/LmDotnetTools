using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Generic workflow transport over the existing hosted conversation and accepted-input protocol.</summary>
internal sealed class WorkflowAgentInvoker(
    string workspace,
    Func<WorkflowInvocation, bool, CancellationToken, Task<S2SReviewAgent?>> resolveSession,
    Func<WorkflowInvocation, string, CancellationToken, Task<WorkflowInvocationResult?>> settle
) : IWorkflowTaskInvoker
{
    public Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default) =>
        RunAsync(invocation, reconcile: false, ct);

    public Task<WorkflowInvocationResult> ReconcileAsync(
        WorkflowInvocation invocation,
        CancellationToken ct = default
    ) => RunAsync(invocation, reconcile: true, ct);

    /// <summary>A stable, bounded host key. Correction options are also part of the host's derived input id.</summary>
    internal static string InvocationKey(WorkflowInvocation invocation) =>
        "workflow:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.InvocationId)));

    private async Task<WorkflowInvocationResult> RunAsync(
        WorkflowInvocation invocation,
        bool reconcile,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Task.Delegate != DelegateKind.Agent || string.IsNullOrWhiteSpace(invocation.SessionId))
            return new(
                WorkflowInvocationStatus.Failed,
                Error: "An agent invocation requires a persisted named session."
            );
        if (!reconcile && invocation.DeadlineUtc is { } expired && expired <= DateTimeOffset.UtcNow)
            return new(WorkflowInvocationStatus.Failed, Error: "The invocation deadline elapsed before dispatch.");

        var submitted = false;
        S2SReviewAgent? agent = null;
        try
        {
            // Resolver must persist the hosted thread binding before its first send. Reconciliation may
            // only reopen that binding; it cannot replace an absent conversation with a fresh parent.
            agent = await resolveSession(invocation, !reconcile, ct).ConfigureAwait(false);
            if (agent is null || string.IsNullOrWhiteSpace(agent.ThreadId))
                return new(WorkflowInvocationStatus.Unknown, Error: "The persisted hosted session is unavailable.");
            if (invocation.DeadlineUtc is { } deadline)
                agent.UseDeadline(deadline);
            if (invocation.IsCorrection || invocation.Task.MaxValidationRetries > 0)
                await agent.EnsureActionToolSuppressionAsync(ct).ConfigureAwait(false);

            var key = InvocationKey(invocation);
            var prompt = reconcile
                ? "Reconcile the already accepted workflow input."
                : await ComposeInstructionAsync(invocation, ct).ConfigureAwait(false);
            agent.ArmTurnCheckpoint(
                key,
                reconcile ? IdempotentInputId.Create(key, invocation.IsCorrection, invocation.IsCorrection) : null,
                onInputAccepted: null
            );
            var input = new UserInput(
                [new TextMessage { Role = Role.User, Text = prompt }],
                SuppressSubAgentSpawning: invocation.IsCorrection,
                SuppressActionTools: invocation.IsCorrection
            );
            submitted = true;
            TextMessage? final = null;
            await foreach (var message in agent.ExecuteRunAsync(input, ct).ConfigureAwait(false))
            {
                if (message is not TextMessage { Role: Role.Assistant, IsThinking: false } text)
                    continue;
                if (final is not null)
                    throw new InvalidOperationException("Multiple final workflow responses were received.");
                final = text;
            }
            if (final is null || final.ThreadId != agent.ThreadId || final.RunId != agent.CurrentRunId)
                return new(
                    WorkflowInvocationStatus.Unknown,
                    Error: "No final response correlated to this accepted invocation."
                );

            // Null is an explicit confirmation from the host's mandatory settlement callback. Receipts
            // and descendant status come from their existing owners, never from model Output fields.
            var unsettled = await settle(invocation, agent.ThreadId, ct).ConfigureAwait(false);
            return unsettled ?? new WorkflowInvocationResult(WorkflowInvocationStatus.Completed, final.Text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is HttpRequestException
                        or TimeoutException
                        or OperationCanceledException
                        or InvalidOperationException
                        or IOException
                        or ArgumentException
            )
        {
            if (
                submitted
                && agent?.CurrentRunStatus is { } status
                && !string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
            )
            {
                try
                {
                    var unsettled = await settle(invocation, agent.ThreadId, ct).ConfigureAwait(false);
                    return unsettled
                        ?? new WorkflowInvocationResult(WorkflowInvocationStatus.Failed, Error: Diagnostic(ex));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception settlementError) when (settlementError is not OutOfMemoryException)
                {
                    return new(WorkflowInvocationStatus.Unknown, Error: Diagnostic(settlementError));
                }
            }
            return new(
                submitted || reconcile ? WorkflowInvocationStatus.Unknown : WorkflowInvocationStatus.Failed,
                Error: Diagnostic(ex)
            );
        }
    }

    internal static string Diagnostic(Exception error) =>
        $"Workflow operation could not be confirmed ({error.GetType().Name}).";

    private async Task<string> ComposeInstructionAsync(WorkflowInvocation invocation, CancellationToken ct)
    {
        var task = invocation.Task;
        if (task.OutputSchema is null)
            throw new InvalidOperationException("Agent invocation requires an output schema.");
        var parts = new List<string>();
        foreach (var skill in task.Skills ?? [])
        {
            var path = WorkflowScriptInvoker.ResolveWorkspaceAsset(skill, Path.GetFullPath(workspace));
            parts.Add(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        parts.Add(task.PromptTemplate);
        parts.Add("Input:\n" + invocation.Input.ToJsonString());
        parts.Add("Output schema:\n" + task.OutputSchema.ToJsonString());
        parts.Add(
            "Return exactly one JSON object matching this schema as your final answer. "
                + "Use the declared JSON types. No Markdown fences, extra fields or surrounding prose."
        );
        if (invocation.IsCorrection)
        {
            if (!string.IsNullOrWhiteSpace(invocation.ValidationError))
                parts.Add("Output validation errors:\n" + invocation.ValidationError);
            parts.Add(
                "Your previous final JSON did not satisfy the output contract. Return corrected JSON using your "
                    + "completed analysis. Do not repeat tool calls or publish anything during this format correction. "
                    + "Action tools are disabled for this turn."
            );
        }
        return string.Join("\n\n", parts);
    }
}
