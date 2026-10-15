using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Production composition of trusted scripts and hosted agent sessions, with correlated output evidence.</summary>
internal sealed class ReviewWorkflowInvoker(
    ReviewRun run,
    string instanceId,
    string runDirectory,
    string packageRoot,
    WorkflowScriptInvoker scripts,
    WorkflowOperationDispatcher dispatcher,
    LmStreamingS2SClient client,
    WorkflowWorkspace workspace,
    ReviewStore store,
    CodeReviewDaemonOptions options,
    WorkflowPublicationScopes publicationScopes,
    Func<ReviewRun, string, CancellationToken, Task<ReviewPublicationTools>> scopeFactory,
    IGatewaySkillProbe gatewaySkillProbe,
    ILoggerFactory loggerFactory
) : IWorkflowTaskInvoker
{
    private ReviewPublicationTools? _publicationTools;
    private IDisposable? _publicationRegistration;

    private sealed record SessionBinding(
        string SessionId,
        string ThreadId,
        string WorkspaceId,
        string ModeId,
        int PublicationPolicyVersion,
        string? WorkingDirectoryRelPath = null
    );

    private sealed record RawResult(int SchemaVersion, WorkflowInvocation Invocation, WorkflowInvocationResult Result);

    public Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default) =>
        ExecuteAsync(invocation, false, ct);

    public Task<WorkflowInvocationResult> ReconcileAsync(
        WorkflowInvocation invocation,
        CancellationToken ct = default
    ) => ExecuteAsync(invocation, true, ct);

    private async Task<WorkflowInvocationResult> ExecuteAsync(
        WorkflowInvocation invocation,
        bool reconcile,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var callerToken = ct;
        if (invocation.InstanceId != instanceId)
            return new(WorkflowInvocationStatus.Failed, Error: "Invocation does not belong to this workflow instance.");
        try
        {
            var evidence = await ReadResultAsync(invocation, ct).ConfigureAwait(false);
            var active = await SettleCompletedSessionsAsync(
                    ct,
                    invocation,
                    reconcilingCurrent: reconcile && evidence?.Status != WorkflowInvocationStatus.Completed
                )
                .ConfigureAwait(false);
            if (active is not null)
                return active;
            if (evidence is { Status: WorkflowInvocationStatus.Completed or WorkflowInvocationStatus.Failed })
                return evidence;
            if (invocation.Task.Delegate == DelegateKind.Agent && invocation.DeadlineUtc is null)
                return new(WorkflowInvocationStatus.Failed, Error: "Agent invocation requires a persisted deadline.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!reconcile && invocation.DeadlineUtc is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return new(
                        WorkflowInvocationStatus.Failed,
                        Error: "The invocation deadline elapsed before dispatch."
                    );
                budget.CancelAfter(remaining);
                ct = budget.Token;
            }

            WorkflowInvocationResult result;
            if (invocation.Task.Delegate == DelegateKind.Agent)
            {
                var admission = await AdmitReviewerCapabilitiesAsync(ct).ConfigureAwait(false);
                if (admission is not null)
                {
                    return admission;
                }
                var agent = new WorkflowAgentInvoker(
                    packageRoot,
                    ResolveSessionAsync,
                    SettleAsync,
                    synthesisIntent: SynthesisIntentAsync
                );
                result = reconcile
                    ? await agent.ReconcileAsync(invocation, ct).ConfigureAwait(false)
                    : await agent.InvokeAsync(invocation, ct).ConfigureAwait(false);
            }
            else if (
                invocation.Task.Delegate == DelegateKind.Script
                && !string.IsNullOrWhiteSpace(invocation.Task.Script)
                && !invocation.IsCorrection
            )
            {
                var context = ScriptContext(invocation);
                if (reconcile)
                {
                    var proof = await dispatcher
                        .ReconcileAsync(
                            Path.GetFileNameWithoutExtension(invocation.Task.Script),
                            context,
                            invocation.Input,
                            ct
                        )
                        .ConfigureAwait(false);
                    result = proof is null
                        ? new(
                            WorkflowInvocationStatus.Unknown,
                            Error: "No durable proof confirms this script invocation."
                        )
                        : new(WorkflowInvocationStatus.Completed, proof.ToJsonString());
                }
                else
                {
                    try
                    {
                        string output;
                        var operation = Path.GetFileNameWithoutExtension(invocation.Task.Script);
                        if (WorkflowOperationDispatcher.Supports(operation))
                        {
                            var dispatched = await dispatcher
                                .DispatchAsync(operation, context, invocation.Input, ct)
                                .ConfigureAwait(false);
                            output = dispatched.ToJsonString();
                        }
                        else
                        {
                            output = await scripts
                                .InvokeAsync(invocation.Task.Script, packageRoot, context, invocation.Input, ct)
                                .ConfigureAwait(false);
                        }
                        result = new(WorkflowInvocationStatus.Completed, output);
                    }
                    catch (Exception ex)
                        when (ex is InvalidOperationException and not WorkflowScriptTerminationException
                            || ex is Win32Exception
                        )
                    {
                        // A closure failure may follow a successful main push. Without its exact integration
                        // proof, retain uncertainty instead of authorizing disposable reset and fresh model work.
                        result = new(
                            ex is ReviewPublicationUncertainException
                            || HasUnresolvedReceipt("workflow-")
                            || Path.GetFileNameWithoutExtension(invocation.Task.Script) == "fetch-discussion"
                            || (
                                Path.GetFileNameWithoutExtension(invocation.Task.Script) == "close-artifact-branch"
                                && !HasConfirmedIntegration(invocation)
                            )
                                ? WorkflowInvocationStatus.Unknown
                                : WorkflowInvocationStatus.Failed,
                            Error: WorkflowAgentInvoker.Diagnostic(ex)
                        );
                    }
                }
            }
            else
                result = new(WorkflowInvocationStatus.Failed, Error: "Unsupported workflow invocation delegate.");

            if (result.Status == WorkflowInvocationStatus.Unknown && invocation.Task.Delegate == DelegateKind.Agent)
                result = await ClassifyInactiveAttemptAsync(invocation, callerToken).ConfigureAwait(false) ?? result;
            if (result.Status == WorkflowInvocationStatus.Completed && invocation.Task.Delegate == DelegateKind.Agent)
                result = await ValidateAgentResultAsync(invocation, result, ct).ConfigureAwait(false);
            await SaveResultAsync(invocation, result, ct).ConfigureAwait(false);
            if (result.Status is WorkflowInvocationStatus.Completed or WorkflowInvocationStatus.Failed)
            {
                _publicationRegistration?.Dispose();
                _publicationRegistration = null;
            }
            return result;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkflowScriptTerminationException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or HttpRequestException
                        or OperationCanceledException
                        or InvalidOperationException
                        or ArgumentException
                        or JsonException
            )
        {
            // A missing response/evidence does not authorize replaying a script that may have committed
            // an external effect. Reconciliation must recover its receipt or keep the invocation blocked.
            if (invocation.Task.Delegate == DelegateKind.Agent)
            {
                var failed = await ClassifyInactiveAttemptAsync(invocation, callerToken).ConfigureAwait(false);
                if (failed is not null)
                {
                    await SaveResultAsync(invocation, failed, callerToken).ConfigureAwait(false);
                    return failed;
                }
            }
            return new(WorkflowInvocationStatus.Unknown, Error: WorkflowAgentInvoker.Diagnostic(ex));
        }
    }

    /// <summary>
    /// Checks the gateway catalog before the host can provision or accept a review turn. The instruction may
    /// ask the model to load and dispatch reviewers, but only this catalog read proves that those prerequisites
    /// exist in the exact marketplace set attached to the hosted review workspace.
    /// </summary>
    private async Task<WorkflowInvocationResult?> AdmitReviewerCapabilitiesAsync(CancellationToken ct)
    {
        try
        {
            var marketplaces = string.IsNullOrWhiteSpace(options.LmStreamingReviewMarketplace)
                ? (IReadOnlyList<string>)[]
                : [options.LmStreamingReviewMarketplace];
            var support = await gatewaySkillProbe.ProbeAsync(marketplaces, ct).ConfigureAwait(false);
            if (support.IsSupported)
            {
                return null;
            }

            return new(
                WorkflowInvocationStatus.Failed,
                Error: "The review workflow requires a supported reviewer catalog: " + support.Describe()
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A probe is read-only, but accepting a turn after it failed would turn an unknown catalog into a
            // shallow review. Keep the invocation retryable while preventing any provision or host send.
            return new(
                WorkflowInvocationStatus.Unknown,
                Error: "The reviewer capability catalog could not be verified. " + WorkflowAgentInvoker.Diagnostic(ex)
            );
        }
    }

    private async Task<S2SReviewAgent?> ResolveSessionAsync(
        WorkflowInvocation invocation,
        bool allowCreate,
        CancellationToken ct
    )
    {
        var session = invocation.SessionId ?? throw new InvalidOperationException("Agent session identity is missing.");
        var kind = "workflow-session:" + Hash(session);
        var artifact = store.TryGetLatestArtifact(run.Id, kind);
        if (artifact is null && !allowCreate)
            return null;
        var prepared = workspace.ReadPreparation(run).Workspace;
        var requestedModel = string.IsNullOrWhiteSpace(invocation.Task.ModelId) ? null : invocation.Task.ModelId;
        if (
            artifact is not null
            && requestedModel is not null
            && !string.Equals(artifact.Provider, requestedModel, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException(
                "The authored model conflicts with the persisted parent session model."
            );
        var providerId =
            artifact?.Provider
            ?? requestedModel
            ?? (string.IsNullOrWhiteSpace(run.ModelId) ? options.LmStreamingProviderId : run.ModelId);
        await client
            .EnsureWorkflowPublicationAsync(
                ct,
                providerId,
                options.LmStreamingModeId,
                requireSandboxEnv: options.WorkspaceEnv.Count > 0,
                requireConversationWorkingDirectory: true
            )
            .ConfigureAwait(false);
        SessionBinding binding;
        if (artifact is null)
        {
            var threadId = await client
                .ProvisionAsync(
                    prepared.WorkspaceId,
                    providerId,
                    options.LmStreamingModeId,
                    systemPromptAppendix: null,
                    options.SubAgentModelId,
                    options.ToolAssistedReasoningEffort,
                    ct,
                    options.WorkspaceEnv,
                    prepared.WorkingDirectoryRelPath
                )
                .ConfigureAwait(false);
            binding = new(
                session,
                threadId,
                prepared.WorkspaceId,
                options.LmStreamingModeId,
                1,
                prepared.WorkingDirectoryRelPath
            );
            // Provision has no model turn. Persist its identity before the adapter may enqueue one.
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = 1,
                    Provider = providerId,
                    Payload = JsonSerializer.Serialize(binding),
                }
            );
            if (options.DeepLinkRetentionHours > 0)
                store.RecordDeepLinkConversation(
                    threadId,
                    $"Review PR #{run.PrId}: {invocation.Task.Label ?? invocation.Task.Id}"
                );
        }
        else
        {
            binding =
                JsonSerializer.Deserialize<SessionBinding>(artifact.Payload)
                ?? throw new InvalidOperationException("Persisted agent session is invalid.");
            if (
                binding.SessionId != session
                || binding.WorkspaceId != prepared.WorkspaceId
                || string.IsNullOrWhiteSpace(binding.ThreadId)
                || binding.PublicationPolicyVersion != 1
                || binding.ModeId != options.LmStreamingModeId
                || binding.WorkingDirectoryRelPath != prepared.WorkingDirectoryRelPath
            )
                throw new InvalidOperationException("Persisted agent session does not match the prepared workspace.");
        }

        var tools = await scopeFactory(run, instanceId, ct).ConfigureAwait(false);
        tools.BindInvocation(invocation, await ReadAdmittedDiscussionAsync(ct).ConfigureAwait(false));
        _publicationTools = tools;
        var registration = publicationScopes.Register(binding.ThreadId, invocation, tools);
        _publicationRegistration?.Dispose();
        _publicationRegistration = registration;
        return new S2SReviewAgent(
            client,
            prepared.WorkspaceId,
            providerId,
            options.LmStreamingModeId,
            systemPrompt: null,
            title: null,
            loggerFactory.CreateLogger<S2SReviewAgent>(),
            overallTimeout: TimeSpan.FromMinutes(options.ReviewStageDeadlineMinutes),
            existingThreadId: binding.ThreadId,
            subAgentModelId: options.SubAgentModelId,
            reasoningEffort: options.ToolAssistedReasoningEffort,
            env: options.WorkspaceEnv,
            workingDirectoryRelPath: prepared.WorkingDirectoryRelPath
        );
    }

    /// <summary>Recheck completed session owners before a later phase or a validated-runtime resume.</summary>
    internal async Task<WorkflowInvocationResult?> SettleCompletedSessionsAsync(
        CancellationToken ct,
        WorkflowInvocation? current = null,
        WorkflowInstanceSnapshot? snapshot = null,
        bool reconcilingCurrent = false,
        bool includeFailed = false
    )
    {
        try
        {
            if (snapshot is not null)
                await ValidateCompletedEvidenceAsync(snapshot, ct).ConfigureAwait(false);
            var directory = Path.Combine(Path.GetFullPath(runDirectory), "private");
            if (!Directory.Exists(directory))
                return null;
            var sessions = new Dictionary<string, RawResult>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(directory, "invocation-*.json"))
            {
                ct.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length > checked(options.Limits.MaxArtifactPayloadChars * 4L))
                    throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
                var saved = JsonSerializer.Deserialize<RawResult>(
                    await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
                );
                if (saved is null || saved.SchemaVersion != 1 || saved.Invocation.InstanceId != instanceId)
                    throw new InvalidOperationException("Invocation artifact identity is invalid.");
                var previous = saved.Invocation;
                if (
                    (
                        saved.Result.Status != WorkflowInvocationStatus.Completed
                        && !(includeFailed && saved.Result.Status == WorkflowInvocationStatus.Failed)
                    )
                    || previous.Task.Delegate != DelegateKind.Agent
                    || previous.SessionId is not { } session
                )
                    continue;
                if (
                    !sessions.TryGetValue(session, out var latest)
                    || previous.DeadlineUtc > latest.Invocation.DeadlineUtc
                    || (
                        previous.DeadlineUtc == latest.Invocation.DeadlineUtc
                        && previous.Attempt > latest.Invocation.Attempt
                    )
                )
                    sessions[session] = saved;
            }
            foreach (var (session, latest) in sessions)
            {
                // Reconciliation owns an unfinished current input. An older completed turn on that
                // same parent must not impose its expired deadline on the still-running newer turn.
                if (reconcilingCurrent && current?.SessionId == session)
                    continue;
                var previous = latest.Invocation;
                var artifact = store.TryGetLatestArtifact(run.Id, "workflow-session:" + Hash(session));
                var binding = artifact is null ? null : JsonSerializer.Deserialize<SessionBinding>(artifact.Payload);
                var prepared = workspace.ReadPreparation(run).Workspace;
                if (
                    binding is null
                    || binding.SessionId != session
                    || string.IsNullOrWhiteSpace(binding.ThreadId)
                    || binding.WorkspaceId != prepared.WorkspaceId
                    || binding.ModeId != options.LmStreamingModeId
                    || binding.WorkingDirectoryRelPath != prepared.WorkingDirectoryRelPath
                    || binding.PublicationPolicyVersion != 1
                )
                    return new(
                        WorkflowInvocationStatus.Unknown,
                        Error: "Completed agent session binding is unavailable."
                    );
                var activityDeadline = previous.DeadlineUtc;
                if (
                    current?.DeadlineUtc is { } currentDeadline
                    && (activityDeadline is null || currentDeadline < activityDeadline)
                )
                    activityDeadline = currentDeadline;
                var pending = await WaitForActivityAsync(binding.ThreadId, activityDeadline, ct).ConfigureAwait(false);
                if (pending is not null)
                    return pending;
            }
            return sessions.Count > 0 && HasUnresolvedReceipt("workflow-publication")
                ? new(WorkflowInvocationStatus.Unknown, Error: "A publication receipt is unresolved.")
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(WorkflowInvocationStatus.Unknown, Error: WorkflowAgentInvoker.Diagnostic(ex));
        }
    }

    private async Task ValidateCompletedEvidenceAsync(WorkflowInstanceSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.InstanceId != instanceId || snapshot.Definition is null)
            throw new InvalidOperationException("Workflow completion evidence belongs to another instance.");
        foreach (var occurrence in snapshot.Tasks.Where(task => task.Status == WorkflowTaskStatus.Validated))
        {
            var task = snapshot
                .Definition.Nodes.OfType<ProceduralNode>()
                .SingleOrDefault(node => node.Id == occurrence.NodeId)
                ?.TaskList?.SingleOrDefault(value => value.Id == occurrence.TaskId);
            if (task?.Delegate != DelegateKind.Agent)
                continue;
            var id = occurrence.ToolCallId ?? $"{instanceId}/{occurrence.Name}/{occurrence.Attempts}";
            var path = Path.Combine(runDirectory, "private", "invocation-" + Hash(id) + ".json");
            if (!File.Exists(path) || new FileInfo(path).Length > checked(options.Limits.MaxArtifactPayloadChars * 4L))
                throw new InvalidOperationException("Validated agent invocation evidence is unavailable.");
            var saved = JsonSerializer.Deserialize<RawResult>(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
            );
            if (
                saved is null
                || saved.SchemaVersion != 1
                || saved.Result.Status != WorkflowInvocationStatus.Completed
                || saved.Invocation.InstanceId != instanceId
                || saved.Invocation.InvocationId != id
                || saved.Invocation.UnitName != occurrence.Name
                || saved.Invocation.Task.Id != occurrence.TaskId
                || saved.Invocation.Attempt != occurrence.Attempts + 1
                || saved.Invocation.SessionId != snapshot.Sessions[task.Session ?? occurrence.NodeId]
                || saved.Invocation.DeadlineUtc != snapshot.Deadlines[occurrence.Name]
            )
                throw new InvalidOperationException("Validated agent invocation evidence does not match the snapshot.");
        }
    }

    private async Task<WorkflowInvocationResult?> SettleAsync(
        WorkflowInvocation invocation,
        string threadId,
        CancellationToken ct
    )
    {
        await (_publicationTools ?? throw new InvalidOperationException("Publication receipt scope is unavailable."))
            .ReconcileAsync(ct)
            .ConfigureAwait(false);
        if (HasUnresolvedReceipt("workflow-publication"))
            return new(WorkflowInvocationStatus.Unknown, Error: "A publication receipt is unresolved.");
        return await WaitForActivityAsync(threadId, invocation.DeadlineUtc, ct).ConfigureAwait(false)
            ?? (
                HasUnresolvedReceipt("workflow-publication")
                    ? new(WorkflowInvocationStatus.Unknown, Error: "A publication receipt is unresolved.")
                    : null
            );
    }

    private async Task<WorkflowInvocationResult?> ClassifyInactiveAttemptAsync(
        WorkflowInvocation invocation,
        CancellationToken ct
    )
    {
        if (
            invocation.DeadlineUtc is not { } deadline
            || deadline > DateTimeOffset.UtcNow
            || invocation.SessionId is not { } session
            || workspace.HasUnsettledPreparation(run.Id)
            || store
                .GetOutboxForRun(run.Id)
                .Any(entry => entry.Status is not (OutboxStatus.Posted or OutboxStatus.Sent or OutboxStatus.Collected))
            || (
                store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset-intent") is not null
                && store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset") is null
            )
        )
            return null;
        try
        {
            _ = await ReadResultAsync(invocation, ct).ConfigureAwait(false);
            var artifact = store.TryGetLatestArtifact(run.Id, "workflow-session:" + Hash(session));
            var binding = artifact is null ? null : JsonSerializer.Deserialize<SessionBinding>(artifact.Payload);
            var prepared = workspace.ReadPreparation(run).Workspace;
            if (
                binding is null
                || binding.SessionId != session
                || string.IsNullOrWhiteSpace(binding.ThreadId)
                || binding.WorkspaceId != prepared.WorkspaceId
                || binding.ModeId != options.LmStreamingModeId
                || binding.WorkingDirectoryRelPath != prepared.WorkingDirectoryRelPath
                || binding.PublicationPolicyVersion != 1
            )
                return null;
            var key = WorkflowAgentInvoker.InvocationKey(invocation);
            var original = await client
                .GetStatusByInputIdAsync(
                    binding.ThreadId,
                    AchieveAi.LmDotnetTools.LmMultiTurn.Messages.IdempotentInputId.Create(
                        key,
                        invocation.IsCorrection,
                        invocation.IsCorrection
                    ),
                    ct
                )
                .ConfigureAwait(false);
            if (original.Status is not ("Completed" or "Errored") || string.IsNullOrWhiteSpace(original.RunId))
                return null;
            if (await SynthesisIntentAsync(invocation, binding.ThreadId, false, ct).ConfigureAwait(false))
            {
                var inputId = AchieveAi.LmDotnetTools.LmMultiTurn.Messages.IdempotentInputId.Create(
                    key + ":synthesis",
                    true,
                    true
                );
                try
                {
                    var finalization = await client
                        .GetStatusByInputIdAsync(binding.ThreadId, inputId, ct)
                        .ConfigureAwait(false);
                    if (
                        finalization.Status is not ("Completed" or "Errored")
                        || string.IsNullOrWhiteSpace(finalization.RunId)
                    )
                        return null;
                }
                catch (HttpRequestException)
                {
                    await client.RequireUnacceptedInputAsync(binding.ThreadId, inputId, ct).ConfigureAwait(false);
                }
            }
            if (await WaitForActivityAsync(binding.ThreadId, deadline, ct).ConfigureAwait(false) is not null)
                return null;
            if (
                store
                    .GetOutboxForRun(run.Id)
                    .Any(entry =>
                        entry.Status is not (OutboxStatus.Posted or OutboxStatus.Sent or OutboxStatus.Collected)
                    )
            )
                return null;
            return new(
                WorkflowInvocationStatus.Failed,
                Error: "Expired hosted attempt is inactive; disposable work will reset."
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or HttpRequestException
                        or InvalidOperationException
                        or ArgumentException
                        or JsonException
            )
        {
            return null;
        }
    }

    private async Task<WorkflowInvocationResult?> WaitForActivityAsync(
        string threadId,
        DateTimeOffset? deadlineUtc,
        CancellationToken ct
    )
    {
        var barrier = new ReviewSubAgentCompletionBarrier(
            new S2SReviewSubAgentCompletionSource(client, includeParentActivity: true),
            TimeSpan.FromSeconds(options.ReviewSubAgentBarrierQuietSeconds),
            loggerFactory.CreateLogger<ReviewSubAgentCompletionBarrier>(),
            unknownQuiescence: TimeSpan.Zero
        );
        var deadline =
            deadlineUtc ?? throw new InvalidOperationException("Agent invocation requires a persisted deadline.");
        if (deadline <= DateTimeOffset.UtcNow)
        {
            if (await barrier.TryConfirmSettledAsync(run, threadId, ct).ConfigureAwait(false) is null)
                return new(
                    WorkflowInvocationStatus.Unknown,
                    Error: "Parent or descendant work remains unresolved after the invocation deadline."
                );
        }
        else
            await barrier.WaitAsync(run, threadId, deadline, _ => Task.CompletedTask, ct).ConfigureAwait(false);
        return null;
    }

    private async Task<JsonArray> ReadAdmittedDiscussionAsync(CancellationToken ct)
    {
        var path = WorkflowScriptInvoker.ResolveWorkspaceAsset("scope.json", Path.GetFullPath(runDirectory));
        if (!File.Exists(path))
            return [];
        var scope = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        if (
            scope?["ReviewRunId"]?.GetValue<long>() != run.Id
            || scope["WorkflowInstanceId"]?.GetValue<string>() != instanceId
        )
            throw new InvalidOperationException("Discussion scope does not match the admitted workflow.");
        return scope["FrozenContext"]?["CommentWindow"]?.DeepClone() as JsonArray ?? [];
    }

    private async Task<WorkflowInvocationResult> ValidateAgentResultAsync(
        WorkflowInvocation invocation,
        WorkflowInvocationResult result,
        CancellationToken ct
    )
    {
        JsonObject? output;
        try
        {
            output = JsonNode.Parse(result.Output ?? "null") as JsonObject;
        }
        catch (JsonException)
        {
            return result; // Preserve the runtime's existing syntax-correction path.
        }
        if (output is null)
            return result; // The runtime owns JSON/schema correction.
        if (invocation.Task.OutputSchema?["properties"]?["Assessments"] is not null)
            ValidateAssessmentIds(invocation.Input, output);
        if (invocation.Task.OutputSchema?["properties"]?["Outcome"] is not null)
            await (_publicationTools ?? throw new InvalidOperationException("Publication scope is missing."))
                .ValidateOutcomeAsync(output, ct)
                .ConfigureAwait(false);
        return result;
    }

    internal static void ValidateAssessmentIds(JsonNode input, JsonObject output)
    {
        var findings =
            input["Review"]?["Findings"] as JsonArray
            ?? throw new InvalidOperationException("Grading requires the admitted findings.");
        var assessments =
            output["Assessments"] as JsonArray
            ?? throw new InvalidOperationException("Grading requires assessments for every finding.");
        var ids = findings.Select(value => value?["Id"]?.GetValue<string>()).ToArray();
        var graded = assessments.Select(value => value?["Id"]?.GetValue<string>()).ToArray();
        if (
            ids.Any(string.IsNullOrWhiteSpace)
            || graded.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || graded.Distinct(StringComparer.Ordinal).Count() != graded.Length
            || !ids.ToHashSet(StringComparer.Ordinal).SetEquals(graded)
        )
            throw new InvalidOperationException("Assessment IDs must match every admitted finding exactly once.");
    }

    private Task<bool> SynthesisIntentAsync(
        WorkflowInvocation invocation,
        string threadId,
        bool create,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var kind = "workflow-synthesis:" + Hash(invocation.InvocationId);
        var expected = new JsonObject
        {
            ["InvocationId"] = invocation.InvocationId,
            ["WorkflowInstanceId"] = invocation.InstanceId,
            ["ThreadId"] = threadId,
            ["OriginalInputId"] = AchieveAi.LmDotnetTools.LmMultiTurn.Messages.IdempotentInputId.Create(
                WorkflowAgentInvoker.InvocationKey(invocation),
                invocation.IsCorrection,
                invocation.IsCorrection
            ),
            ["Key"] = WorkflowAgentInvoker.InvocationKey(invocation) + ":synthesis",
            ["Prompt"] = WorkflowAgentInvoker.FinalizationPrompt(invocation),
            ["DeadlineUtc"] = invocation.DeadlineUtc?.ToString("O"),
            ["SuppressSubAgentSpawning"] = true,
            ["SuppressActionTools"] = true,
        };
        if (store.TryGetLatestArtifact(run.Id, kind) is { } saved)
        {
            if (!JsonNode.DeepEquals(JsonNode.Parse(saved.Payload), expected))
                throw new InvalidDataException("Synthesis intent does not match its invocation.");
            return Task.FromResult(true);
        }
        if (!create)
            return Task.FromResult(false);
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = kind,
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = expected.ToJsonString(),
            }
        );
        return Task.FromResult(true);
    }

    private bool HasConfirmedIntegration(WorkflowInvocation invocation)
    {
        try
        {
            var artifact = store.TryGetLatestArtifact(run.Id, "workflow-content-integration");
            var proof = artifact is null ? null : JsonNode.Parse(artifact.Payload) as JsonObject;
            var branch = invocation.Input["ArtifactBranch"]?.GetValue<string>();
            var retainedSha = invocation.Input["RetainedSha"]?.GetValue<string>();
            return artifact?.ArtifactSchemaVersion == 1
                && invocation.InstanceId == instanceId
                && proof?["WorkflowInstanceId"]?.GetValue<string>() == invocation.InstanceId
                && !string.IsNullOrWhiteSpace(branch)
                && proof["ArtifactBranch"]?.GetValue<string>() == branch
                && IsCommitSha(retainedSha)
                && proof["RetainedSha"]?.GetValue<string>() == retainedSha
                && IsCommitSha(proof["MainSha"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        static bool IsCommitSha(string? value) =>
            value is { Length: 40 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    private bool HasUnresolvedReceipt(string kindPrefix) =>
        store
            .GetOutboxForRun(run.Id)
            .Any(entry =>
                entry.ArtifactKind.StartsWith(kindPrefix, StringComparison.Ordinal)
                && entry.Status is not (OutboxStatus.Posted or OutboxStatus.Sent or OutboxStatus.Collected)
            );

    private JsonObject ScriptContext(WorkflowInvocation invocation) =>
        new()
        {
            ["RunId"] = run.Id.ToString(CultureInfo.InvariantCulture),
            ["StepId"] = invocation.Task.Id,
            ["Attempt"] = invocation.Attempt,
            ["RunDirectory"] = Path.GetFullPath(runDirectory),
        };

    private string ResultPath(WorkflowInvocation invocation) =>
        WorkflowScriptInvoker.ResolveWorkspaceAsset(
            "private/invocation-" + Hash(invocation.InvocationId) + ".json",
            Path.GetFullPath(runDirectory)
        );

    private async Task<WorkflowInvocationResult?> ReadResultAsync(WorkflowInvocation invocation, CancellationToken ct)
    {
        var path = ResultPath(invocation);
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(
                runDirectory,
                "artifacts",
                "invocation-" + Hash(invocation.InvocationId) + ".json"
            );
            if (File.Exists(legacy))
                throw new InvalidOperationException("Legacy invocation evidence requires explicit recovery.");
            return null;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > checked(options.Limits.MaxArtifactPayloadChars * 4L))
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        using var reader = new StreamReader(stream);
        var buffer = new char[options.Limits.MaxArtifactPayloadChars + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (count > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        var saved = JsonSerializer.Deserialize<RawResult>(new string(buffer, 0, count));
        if (
            saved is null
            || saved.SchemaVersion != 1
            || saved.Result is null
            || !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(saved.Invocation),
                JsonSerializer.SerializeToNode(invocation)
            )
        )
            throw new InvalidOperationException("Invocation artifact identity does not match the active invocation.");
        return saved.Result;
    }

    private Task SaveResultAsync(WorkflowInvocation invocation, WorkflowInvocationResult result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new RawResult(1, invocation, result));
        if (json.Length > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        var bytes = Encoding.UTF8.GetBytes(json);
        return AtomicFile.WriteAsync(
            ResultPath(invocation),
            async (temporary, token) =>
            {
                await using var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                );
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            },
            ct
        );
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
