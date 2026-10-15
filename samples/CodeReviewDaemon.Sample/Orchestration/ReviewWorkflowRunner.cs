using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

internal interface IReviewWorkflowRunner
{
    Task<WorkflowInvocationStatus> RunAsync(
        ReviewRun run,
        WorkflowRound? round,
        JsonObject frozenContext,
        CancellationToken cancellationToken
    );

    Task<WorkflowInvocationStatus> ResumeAsync(
        ReviewRun run,
        WorkflowRound? round,
        CancellationToken cancellationToken
    );

    Task<WorkflowInvocationStatus> RunOrResumeAsync(
        ReviewRun run,
        WorkflowRound? round,
        JsonObject currentContext,
        CancellationToken cancellationToken
    );

    Task<JsonObject> ReadFrozenContextAsync(ReviewRun run, WorkflowRound? round, CancellationToken cancellationToken);

    Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken);

    Task RecoverActiveOwnerAsync(ReviewRun run, CancellationToken cancellationToken) => Task.CompletedTask;

    Task<WorkflowInvocationStatus?> ReconcileActiveOwnerAsync(
        ReviewRun run,
        string requestedWorkflowInstanceId,
        CancellationToken cancellationToken
    ) => Task.FromResult<WorkflowInvocationStatus?>(null);

    bool HasFrozenContext(ReviewRun run, WorkflowRound? round);
}

/// <summary>
/// Admits one durable review activity to the authored workflow while keeping its snapshot, frozen scope,
/// parent sessions, and workspace assignment on the same stable identity.
/// </summary>
internal sealed class ReviewWorkflowRunner : IReviewWorkflowRunner
{
    internal const string ScopeFileName = "scope.json";

    private readonly string _workflowPath;
    private readonly string _runDirectoryRoot;
    private readonly ReviewStore _reviewStore;
    private readonly IWorkflowStore _workflowStore;
    private readonly WorkflowWorkspace _workspace;
    private readonly Func<ReviewRun, string, string, CancellationToken, Task<IWorkflowTaskInvoker>> _invokerFactory;
    private readonly Func<ReviewRun, WorkflowRound?, IReadOnlyDictionary<string, string>>? _hostSessionBindings;
    private readonly TimeSpan? _invocationTimeout;
    private readonly bool _resetFailedAttempts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, ReviewSlotClaim> _streamClaims = new();
    private readonly AsyncLocal<bool> _reconcileOnly = new();
    private readonly ILogger<ReviewWorkflowRunner> _logger;

    private sealed class ReconcileOnlyInvoker(IWorkflowTaskInvoker inner) : IWorkflowTaskInvoker
    {
        public Task<WorkflowInvocationResult> InvokeAsync(
            WorkflowInvocation invocation,
            CancellationToken ct = default
        ) =>
            Task.FromResult(
                new WorkflowInvocationResult(
                    WorkflowInvocationStatus.Failed,
                    Error: "Startup recovery discards the unfinished remainder instead of dispatching new work."
                )
            );

        public Task<WorkflowInvocationResult> ReconcileAsync(
            WorkflowInvocation invocation,
            CancellationToken ct = default
        ) => inner.ReconcileAsync(invocation, ct);
    }

    internal void RegisterStreamClaim(long runId, ReviewSlotClaim claim)
    {
        if (!_streamClaims.TryAdd(runId, claim))
            throw new InvalidOperationException("Review already has a stream slot claim.");
    }

    internal void UnregisterStreamClaim(long runId, ReviewSlotClaim claim)
    {
        if (_streamClaims.TryGetValue(runId, out var registered) && ReferenceEquals(registered, claim))
            _streamClaims.TryRemove(runId, out _);
    }

    public ReviewWorkflowRunner(
        string workflowPath,
        string runDirectoryRoot,
        ReviewStore reviewStore,
        IWorkflowStore workflowStore,
        WorkflowWorkspace workspace,
        Func<ReviewRun, string, string, CancellationToken, Task<IWorkflowTaskInvoker>> invokerFactory,
        Func<ReviewRun, WorkflowRound?, IReadOnlyDictionary<string, string>>? hostSessionBindings = null,
        TimeSpan? invocationTimeout = null,
        bool resetFailedAttempts = false,
        ILogger<ReviewWorkflowRunner>? logger = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectoryRoot);
        _workflowPath = Path.GetFullPath(workflowPath);
        _runDirectoryRoot = Path.GetFullPath(runDirectoryRoot);
        _reviewStore = reviewStore ?? throw new ArgumentNullException(nameof(reviewStore));
        _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _invokerFactory = invokerFactory ?? throw new ArgumentNullException(nameof(invokerFactory));
        _hostSessionBindings = hostSessionBindings;
        if (invocationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(invocationTimeout), "Invocation timeout must be positive.");
        }
        _invocationTimeout = invocationTimeout;
        _resetFailedAttempts = resetFailedAttempts;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ReviewWorkflowRunner>.Instance;
    }

    internal async Task<bool> HasStartedRoundAsync(WorkflowRound round, CancellationToken ct)
    {
        if (round.WorkflowInstanceId is not { } instance)
            return false;
        return await _workflowStore.LoadAsync(instance, ct).ConfigureAwait(false) is not null;
    }

    internal async Task<JsonObject> PrepareMergedOnlyAsync(
        ReviewRun run,
        WorkflowRound round,
        JsonObject frozenContext,
        CancellationToken ct,
        int? slotIndex = null
    )
    {
        var stored = ReadAndValidateRun(run);
        var storedRound = ReadAndValidateRound(stored, round, frozenContext)!;
        if (storedRound.Kind != WorkflowRoundKind.Merged)
            throw new InvalidOperationException("Inspection requires a merged completion round.");
        var instance = InstanceIdFor(stored, storedRound);
        if (await _workflowStore.LoadAsync(instance, ct) is not null)
            throw new InvalidOperationException("An executing workflow cannot be replaced by preparation inspection.");
        var admission = BuildAdmission(stored, storedRound);
        await WriteOrVerifyScopeAsync(RunDirectory(instance), stored.Id, instance, admission, frozenContext, ct);
        _ = await WorkflowPackageSnapshot.PrepareAsync(_workflowPath, RunDirectory(instance), false, ct);
        if (slotIndex is { } selectedSlot)
            await _workspace.SelectMergedInspectionSlotAsync(stored, instance, selectedSlot, ct);
        if (!await AcquireOrRecoverAsync(stored, instance, ct))
            throw new WorkspaceCapacityUnavailableException();
        var settled = false;
        try
        {
            var output = await _workspace.ReconcilePreparationAsync(stored, admission, instance, ct);
            output ??= (await _workspace.PrepareAssignedAsync(stored, admission, instance, ct)).Output;
            settled = true;
            return new JsonObject
            {
                ["Outcome"] = "prepared_only",
                ["RunId"] = stored.Id,
                ["RoundId"] = storedRound.Id,
                ["Preparation"] = output.DeepClone(),
                ["Published"] = false,
            };
        }
        finally
        {
            await _workspace.ReleaseOrRetireAsync(stored.Id, instance, settled, CancellationToken.None);
        }
    }

    internal async Task RecoverUnsubmittedMergedAssessmentAsync(
        ReviewRun run,
        WorkflowRound round,
        JsonObject frozenContext,
        CodeReviewDaemon.Sample.Agents.LmStreamingS2SClient client,
        CancellationToken ct
    )
    {
        run = ReadAndValidateRun(run);
        round = ReadAndValidateRound(run, round, frozenContext)!;
        var instance = InstanceIdFor(run, round);
        var saved = await _workflowStore.LoadAsync(instance, ct);
        if (saved?.CurrentNodeId != "merged-self-assessment")
            return;
        var failed = saved.Tasks.SingleOrDefault(t => t.NodeId == saved.CurrentNodeId);
        if (failed?.Status != WorkflowTaskStatus.Failed)
            return;
        var admission = BuildAdmission(run, round);
        ValidateSnapshot(saved, instance, admission);
        await WriteOrVerifyScopeAsync(RunDirectory(instance), run.Id, instance, admission, frozenContext, ct);
        _ = await WorkflowPackageSnapshot.PrepareAsync(_workflowPath, RunDirectory(instance), true, ct);
        var failedId = failed.ToolCallId ?? $"{instance}/{failed.Name}/{failed.Attempts}";
        var receiptPath = Path.Combine(
            RunDirectory(instance),
            "private",
            "invocation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(failedId))) + ".json"
        );
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, ct))!;
        var invocation = JsonSerializer.Deserialize<WorkflowInvocation>(receipt["Invocation"]!)!;
        var task = saved
            .Definition!.Nodes.OfType<ProceduralNode>()
            .Single(n => n.Id == saved.CurrentNodeId)
            .TaskList?.SingleOrDefault();
        if (
            run.ReviewKind != "merged-completion"
            || run.Mode != "collect-only"
            || run.PrLifecycleState != PrLifecycleState.Merged
            || round.Kind != WorkflowRoundKind.Merged
            || round.CompletedAt is not null
            || failed.Attempts != 0
            || failed.Visit != 1
            || failedId.Contains(":unsubmitted-recovery", StringComparison.Ordinal)
            || _reviewStore.TryGetLatestArtifact(run.Id, "workflow-unsubmitted-recovery") is not null
            || saved.Tasks.Count != 2
            || !saved.Tasks.Any(t => t.NodeId == "prepare-history" && t.Status == WorkflowTaskStatus.Validated)
            || task?.Delegate != DelegateKind.Agent
            || task.Id != failed.TaskId
            || invocation.InvocationId != failedId
            || invocation.InstanceId != instance
            || invocation.Task.Id != failed.TaskId
            || invocation.IsCorrection
            || invocation.SessionId != saved.Sessions.GetValueOrDefault(task.Session ?? saved.CurrentNodeId)
            || receipt["Result"]?["Status"]?.GetValue<int>() != (int)WorkflowInvocationStatus.Failed
            || _reviewStore.GetOutboxForRun(run.Id).Any()
        )
            throw new InvalidOperationException("Only an unsubmitted first merged assessment can be recovered once.");
        var bindingKind =
            "workflow-session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.SessionId!)));
        var binding = JsonNode.Parse(
            _reviewStore.TryGetLatestArtifact(run.Id, bindingKind)?.Payload
                ?? throw new InvalidDataException("The failed assessment session is missing.")
        )!;
        var prepared = JsonSerializer.Deserialize<WorkflowWorkspacePreparation>(
            _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.PreparationArtifactKind)?.Payload
                ?? throw new InvalidDataException("The failed assessment preparation is missing.")
        )!;
        var thread =
            binding["ThreadId"]?.GetValue<string>() ?? throw new InvalidDataException("Hosted thread is missing.");
        if (
            binding["SessionId"]?.GetValue<string>() != invocation.SessionId
            || binding["WorkspaceId"]?.GetValue<string>() != prepared.Workspace.WorkspaceId
            || binding["WorkingDirectoryRelPath"]?.GetValue<string>() != prepared.Workspace.WorkingDirectoryRelPath
        )
            throw new InvalidDataException("The failed session does not match its prepared workspace.");
        await client.RequireIdleAsync(thread, ct);
        if (
            (await client.GetConversationTranscriptAsync(thread, ct)).Count != 0
            || (await client.GetSubAgentTreeAsync(thread, ct)).Nodes.Count != 0
        )
            throw new InvalidOperationException("The failed session contains work; reconcile it instead of retrying.");
        var inputId = AchieveAi.LmDotnetTools.LmMultiTurn.Messages.IdempotentInputId.Create(
            WorkflowAgentInvoker.InvocationKey(invocation),
            false,
            false
        );
        await client.RequireUnacceptedInputAsync(thread, inputId, ct);
        if (!await AcquireOrRecoverAsync(run, instance, ct))
            throw new WorkspaceCapacityUnavailableException();
        try
        {
            if (await _workspace.ReconcilePreparationAsync(run, admission, instance, ct) is null)
                throw new InvalidDataException("Prepared evidence must reconcile before recovery.");
            var deadlines = new Dictionary<string, DateTimeOffset>(saved.Deadlines);
            deadlines.Remove(failed.Name);
            var outputs = (JsonObject)saved.Outputs.DeepClone();
            outputs[failed.NodeId]?.AsObject().Remove(failed.TaskId);
            _reviewStore.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = "workflow-unsubmitted-recovery",
                    ArtifactSchemaVersion = 1,
                    Provider = "workflow",
                    Payload = saved.ToJson(),
                }
            );
            await _workflowStore.SaveAsync(
                instance,
                saved with
                {
                    Tasks = saved
                        .Tasks.Select(t =>
                            t == failed
                                ? t with
                                {
                                    Status = WorkflowTaskStatus.Pending,
                                    ToolCallId = failedId + ":unsubmitted-recovery",
                                    LastError = null,
                                }
                                : t
                        )
                        .ToArray(),
                    Deadlines = deadlines,
                    Outputs = outputs,
                },
                ct
            );
        }
        finally
        {
            await _workspace.ReleaseOrRetireAsync(run.Id, instance, true, CancellationToken.None);
        }
    }

    internal async Task RecoverUnstartedMergedClosureAsync(
        ReviewRun run,
        WorkflowRound round,
        JsonObject frozenContext,
        Func<string, CancellationToken, Task> verifyUnstarted,
        CancellationToken ct
    )
    {
        run = ReadAndValidateRun(run);
        round = ReadAndValidateRound(run, round, frozenContext)!;
        var instance = InstanceIdFor(run, round);
        var saved = await _workflowStore.LoadAsync(instance, ct);
        if (saved?.CurrentNodeId != "close-artifact-branch")
            return;
        var failed = saved.Tasks.SingleOrDefault(t => t.NodeId == saved.CurrentNodeId);
        if (failed?.Status != WorkflowTaskStatus.Failed)
            return;
        var admission = BuildAdmission(run, round);
        ValidateSnapshot(saved, instance, admission);
        await WriteOrVerifyScopeAsync(RunDirectory(instance), run.Id, instance, admission, frozenContext, ct);
        _ = await WorkflowPackageSnapshot.PrepareAsync(_workflowPath, RunDirectory(instance), true, ct);
        var failedId = failed.ToolCallId ?? $"{instance}/{failed.Name}/{failed.Attempts}";
        var receiptPath = Path.Combine(
            RunDirectory(instance),
            "private",
            "invocation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(failedId))) + ".json"
        );
        var receipt = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, ct))!;
        var invocation = JsonSerializer.Deserialize<WorkflowInvocation>(receipt["Invocation"]!)!;
        var retained = saved.State["MergedRetention"] as JsonObject;
        var retainedSha = retained?["RetainedSha"]?.GetValue<string>();
        var outbox = _reviewStore.GetOutboxForRun(run.Id);
        if (
            run.ReviewKind != "merged-completion"
            || run.Mode != "collect-only"
            || run.PrLifecycleState != PrLifecycleState.Merged
            || round.Kind != WorkflowRoundKind.Merged
            || round.CompletedAt is not null
            || failed.Attempts != 0
            || failed.Visit != 1
            || failedId.Contains(":closure-recovery", StringComparison.Ordinal)
            || _reviewStore.TryGetLatestArtifact(run.Id, "workflow-closure-recovery") is not null
            || _reviewStore.TryGetLatestArtifact(run.Id, "workflow-content-integration") is not null
            || saved.Tasks.Any(t => t != failed && t.Status != WorkflowTaskStatus.Validated)
            || !saved.Tasks.Any(t => t.NodeId == "retain-merged" && t.Status == WorkflowTaskStatus.Validated)
            || invocation.InvocationId != failedId
            || invocation.InstanceId != instance
            || invocation.Task.Id != failed.TaskId
            || invocation.Task.Delegate != DelegateKind.Script
            || invocation.Task.Script != "scripts/close-artifact-branch.py"
            || invocation.IsCorrection
            || !JsonNode.DeepEquals(invocation.Input, retained)
            || receipt["Result"]?["Status"]?.GetValue<int>() != (int)WorkflowInvocationStatus.Failed
            || retainedSha is not { Length: 40 }
            || !retainedSha.All(Uri.IsHexDigit)
            || retained?["ArtifactBranch"]?.GetValue<string>()
                != frozenContext["Merge"]?["ArtifactBranch"]?.GetValue<string>()
            || outbox.Count != 1
            || outbox[0].Status != OutboxStatus.Posted
            || outbox[0].Operation != "workflow_retain_artifacts"
            || outbox[0].ProviderResponseId != retainedSha
        )
            throw new InvalidOperationException(
                "Only a retained, unstarted first merged closure can be recovered once."
            );
        await verifyUnstarted(retainedSha, ct);
        var deadlines = new Dictionary<string, DateTimeOffset>(saved.Deadlines);
        deadlines.Remove(failed.Name);
        _reviewStore.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = "workflow-closure-recovery",
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = saved.ToJson(),
            }
        );
        await _workflowStore.SaveAsync(
            instance,
            saved with
            {
                Tasks = saved
                    .Tasks.Select(t =>
                        t == failed
                            ? t with
                            {
                                Status = WorkflowTaskStatus.Pending,
                                ToolCallId = failedId + ":closure-recovery",
                                LastError = null,
                            }
                            : t
                    )
                    .ToArray(),
                Deadlines = deadlines,
            },
            ct
        );
    }

    public async Task<WorkflowInvocationStatus> RunAsync(
        ReviewRun run,
        WorkflowRound? round,
        JsonObject frozenContext,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(frozenContext);
        var storedRun = ReadAndValidateRun(run);
        var storedRound = ReadAndValidateRound(storedRun, round, frozenContext);
        var instanceId = storedRound is null ? $"review-run-{storedRun.Id}" : $"review-round-{storedRound.Id}";
        var admission = BuildAdmission(storedRun, storedRound);
        var runDirectory = RunDirectory(instanceId);
        await WriteOrVerifyScopeAsync(
                runDirectory,
                storedRun.Id,
                instanceId,
                admission,
                frozenContext,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (storedRound is not null)
        {
            if (
                storedRound.WorkflowInstanceId is not null
                && !string.Equals(storedRound.WorkflowInstanceId, instanceId, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException("Workflow round is already bound to another workflow instance.");
            }
            if (storedRound.CompletedAt is null && !_reviewStore.TryBindWorkflowInstance(storedRound.Id, instanceId))
            {
                throw new InvalidOperationException(
                    "Workflow round could not be bound to its stable workflow instance."
                );
            }
        }

        var saved = await _workflowStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (saved?.IsComplete == true)
        {
            ValidateSnapshot(saved, instanceId, admission);
            var completedInvoker = await _invokerFactory(storedRun, instanceId, runDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (completedInvoker is ReviewWorkflowInvoker completedHost)
            {
                var pending = await completedHost
                    .SettleCompletedSessionsAsync(cancellationToken, snapshot: saved)
                    .ConfigureAwait(false);
                if (pending is not null)
                    return pending.Status;
            }
            if (_resetFailedAttempts && (storedRound is null || storedRound.Kind == WorkflowRoundKind.Merged))
            {
                var assigned = _reviewStore.TryGetLatestArtifact(
                    storedRun.Id,
                    WorkflowWorkspace.AssignmentArtifactKind
                );
                if (assigned is not null && JsonNode.Parse(assigned.Payload)?["Active"]?.GetValue<bool>() == true)
                {
                    if (!await AcquireOrRecoverAsync(storedRun, instanceId, cancellationToken).ConfigureAwait(false))
                        throw new WorkspaceCapacityUnavailableException();
                    await _workspace
                        .ResetSettledAttemptAsync(storedRun, instanceId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            CompleteAdmission(storedRun, storedRound, instanceId);
            await ReleaseSettledAssignmentIfActiveAsync(storedRun, instanceId, cancellationToken).ConfigureAwait(false);
            return WorkflowInvocationStatus.Completed;
        }
        var admittedWorkflow = await WorkflowPackageSnapshot
            .PrepareAsync(_workflowPath, runDirectory, saved is not null, cancellationToken)
            .ConfigureAwait(false);
        var runtime = saved is null
            ? CreateRuntime(admission, admittedWorkflow, cancellationToken)
            : RestoreRuntime(saved, instanceId, admission);
        runtime.AutomaticInvocationTimeout = _invocationTimeout;
        ApplySessionBindings(runtime, storedRun, storedRound);

        if (storedRound?.CompletedAt is not null)
        {
            throw new InvalidDataException("A completed workflow round is missing its durable completed snapshot.");
        }
        if (
            storedRound is null
            && storedRun.Stage == ReviewStage.Posted
            && storedRun.WorkflowStatus == WorkflowStatus.Completed
        )
        {
            throw new InvalidDataException("A completed review run is missing its durable completed snapshot.");
        }

        var hasLease = false;
        var releaseLease = false;
        var factoryCompleted = false;
        WorkflowInvocationStatus? runtimeStatus = null;
        try
        {
            if (!await AcquireOrRecoverAsync(storedRun, instanceId, cancellationToken).ConfigureAwait(false))
            {
                throw new WorkspaceCapacityUnavailableException();
            }
            hasLease = true;
            if (storedRound is not null)
            {
                _ = _reviewStore.RecordWorkflowOutcome(storedRound.Id, instanceId, WorkflowRoundOutcome.Running);
            }

            var invoker = await _invokerFactory(storedRun, instanceId, runDirectory, cancellationToken)
                .ConfigureAwait(false);
            factoryCompleted = true;
            var pending =
                saved is not null && invoker is ReviewWorkflowInvoker hostedInvoker
                    ? await hostedInvoker
                        .SettleCompletedSessionsAsync(cancellationToken, snapshot: saved)
                        .ConfigureAwait(false)
                    : null;
            runtimeStatus =
                pending?.Status
                ?? await runtime
                    .RunAutomaticAsync(
                        _workflowStore,
                        instanceId,
                        _reconcileOnly.Value ? new ReconcileOnlyInvoker(invoker) : invoker,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            if (runtimeStatus == WorkflowInvocationStatus.Unknown)
            {
                if (storedRound is not null)
                {
                    _ = _reviewStore.RecordWorkflowOutcome(storedRound.Id, instanceId, WorkflowRoundOutcome.Unknown);
                }
                return runtimeStatus.Value;
            }

            var durable =
                await _workflowStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Workflow execution returned without a durable snapshot.");
            ValidateSnapshot(durable, instanceId, admission);
            if (runtimeStatus == WorkflowInvocationStatus.Completed)
            {
                if (!durable.IsComplete)
                {
                    throw new InvalidDataException(
                        "Workflow execution completed without a durable completion snapshot."
                    );
                }
                if (_resetFailedAttempts && (storedRound is null || storedRound.Kind == WorkflowRoundKind.Merged))
                {
                    if (invoker is ReviewWorkflowInvoker completedHost)
                    {
                        var unsettled = await completedHost
                            .SettleCompletedSessionsAsync(cancellationToken, snapshot: durable)
                            .ConfigureAwait(false);
                        if (unsettled is not null)
                            return WorkflowInvocationStatus.Unknown;
                    }
                    await _workspace
                        .ResetSettledAttemptAsync(storedRun, instanceId, cancellationToken)
                        .ConfigureAwait(false);
                }
                releaseLease = true;
                CompleteAdmission(storedRun, storedRound, instanceId);
            }
            else
            {
                releaseLease = IsSettled(durable);
                if (
                    releaseLease
                    && _resetFailedAttempts
                    && (storedRound is null || storedRound.Kind == WorkflowRoundKind.Merged)
                )
                {
                    releaseLease = false;
                    if (invoker is ReviewWorkflowInvoker failedHost)
                    {
                        var unresolved = await failedHost
                            .SettleCompletedSessionsAsync(cancellationToken, snapshot: durable, includeFailed: true)
                            .ConfigureAwait(false);
                        if (unresolved is not null)
                            return WorkflowInvocationStatus.Unknown;
                    }
                    await _workspace
                        .ResetSettledAttemptAsync(storedRun, instanceId, cancellationToken)
                        .ConfigureAwait(false);
                    releaseLease = true;
                }
                if (storedRound is not null)
                {
                    _ = _reviewStore.RecordWorkflowOutcome(storedRound.Id, instanceId, WorkflowRoundOutcome.Failed);
                }
            }
            return runtimeStatus.Value;
        }
        catch (WorkflowScriptTerminationException)
        {
            // A settled snapshot cannot prove OS process containment. Keep this owner's lease.
            releaseLease = false;
            _reviewStore.TryMarkReviewRunParked(
                storedRun.Id,
                DateTimeOffset.UtcNow,
                "Script containment could not be proven. Reconcile processes and workspace manually before unpark."
            );
            throw;
        }
        catch
        {
            if (
                hasLease
                && runtimeStatus != WorkflowInvocationStatus.Unknown
                && !(
                    _resetFailedAttempts
                    && runtimeStatus is WorkflowInvocationStatus.Failed or WorkflowInvocationStatus.Completed
                )
            )
            {
                releaseLease =
                    !factoryCompleted
                    || await HasDurablySettledSnapshotAsync(instanceId, admission).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (hasLease && releaseLease)
            {
                await _workspace
                    .ReleaseOrRetireAsync(storedRun.Id, instanceId, workSettled: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    internal async Task ValidateMergedResumeAsync(
        ReviewRun run,
        WorkflowRound round,
        JsonObject frozen,
        int? slot,
        CancellationToken ct
    )
    {
        var stored = ReadAndValidateRun(run);
        var storedRound = ReadAndValidateRound(stored, round, frozen)!;
        var instance = InstanceIdFor(stored, storedRound);
        var context = await ReadFrozenContextAsync(stored, storedRound, ct);
        var snapshot = await _workflowStore.LoadAsync(instance, ct);
        var admission = BuildAdmission(stored, storedRound);
        if (snapshot is not null)
            ValidateSnapshot(snapshot, instance, admission);
        var assignment = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.AssignmentArtifactKind);
        var prepared = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.PreparationArtifactKind);
        var assigned = assignment is null ? null : JsonNode.Parse(assignment.Payload);
        var preparation = prepared is null ? null : JsonNode.Parse(prepared.Payload);
        if (
            !JsonNode.DeepEquals(context, frozen)
            || snapshot?.IsComplete == true
            || snapshot?.Tasks.Any(task => task.Status == WorkflowTaskStatus.Failed) == true
            || (snapshot is not null && snapshot.State["PreparedHistory"] is null)
            || assigned?["WorkflowInstanceId"]?.GetValue<string>() != instance
            || assigned?["Slot"]?["Index"]?.GetValue<int>() != slot
            || preparation is null
            || !JsonNode.DeepEquals(assigned?["Slot"], preparation["Slot"])
            || !JsonNode.DeepEquals(assigned?["AssignmentId"], preparation["AssignmentId"])
            || !JsonNode.DeepEquals(preparation["Admission"], admission)
            || (snapshot is not null && !JsonNode.DeepEquals(preparation["Output"], snapshot.State["PreparedHistory"]))
        )
            throw new InvalidDataException("Merged resume requires the exact prepared round and slot.");
        _ = await WorkflowPackageSnapshot.PrepareAsync(_workflowPath, RunDirectory(instance), true, ct);
        if (
            !await AcquireOrRecoverAsync(stored, instance, ct)
            || await _workspace.ReconcilePreparationAsync(stored, admission, instance, ct) is null
        )
            throw new InvalidDataException("Merged resume cannot verify the existing remote preparation.");
    }

    /// <summary>Resumes a durable workflow from the immutable context stored in its scope.</summary>
    public async Task<WorkflowInvocationStatus> ResumeAsync(
        ReviewRun run,
        WorkflowRound? round,
        CancellationToken cancellationToken
    )
    {
        var frozenContext = await ReadFrozenContextAsync(run, round, cancellationToken).ConfigureAwait(false);
        return await RunAsync(run, round, frozenContext, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the first immutable scope, or resumes the existing scope when admission already began.</summary>
    public Task<WorkflowInvocationStatus> RunOrResumeAsync(
        ReviewRun run,
        WorkflowRound? round,
        JsonObject initialFrozenContext,
        CancellationToken cancellationToken
    )
    {
        var instanceId = round is null ? $"review-run-{run.Id}" : $"review-round-{round.Id}";
        return File.Exists(Path.Combine(RunDirectory(instanceId), ScopeFileName))
            ? ResumeAsync(run, round, cancellationToken)
            : RunAsync(run, round, initialFrozenContext, cancellationToken);
    }

    /// <summary>Fail-closed gate for an operator's fresh attempt, never an automatic task replay.</summary>
    public async Task<bool> CanStartFreshAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var stored = ReadAndValidateRun(run);
        if (
            stored.WorkflowStatus != WorkflowStatus.RetryPending
            || stored.Stage != ReviewStage.Discovered
            || _reviewStore.ListActiveWorkflowWorkspaceRuns().Any(value => value.Id == stored.Id)
            || WorkflowWorkspace.HasAnyUnsettledPreparation(_reviewStore)
            || _reviewStore.GetOutboxForRun(stored.Id).Count != 0
            || _reviewStore.TryGetLatestArtifact(stored.Id, ReviewArtifactKinds.ArtifactBranchKind) is not null
            || _reviewStore.TryGetLatestArtifact(stored.Id, ReviewArtifactKinds.ArtifactBranchQuarantineKind)
                is not null
        )
            return false;

        try
        {
            _ = await ReadFrozenContextAsync(stored, null, cancellationToken).ConfigureAwait(false);
            var instanceId = InstanceIdFor(stored, null);
            var snapshot = await _workflowStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.IsComplete)
                return false;
            ValidateSnapshot(snapshot, instanceId, BuildAdmission(stored, null));
            return snapshot.Tasks.Any(task => task.Status == WorkflowTaskStatus.Failed)
                && snapshot.Tasks.All(task => task.Status is WorkflowTaskStatus.Failed or WorkflowTaskStatus.Validated);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public bool HasFrozenContext(ReviewRun run, WorkflowRound? round)
    {
        var instanceId = round is null ? $"review-run-{run.Id}" : $"review-round-{round.Id}";
        return File.Exists(Path.Combine(RunDirectory(instanceId), ScopeFileName));
    }

    /// <summary>Reads and validates the immutable context for an admitted workflow instance.</summary>
    public async Task<JsonObject> ReadFrozenContextAsync(
        ReviewRun run,
        WorkflowRound? round,
        CancellationToken cancellationToken
    )
    {
        var storedRun = ReadAndValidateRun(run);
        WorkflowRound? storedRound = null;
        if (round is not null)
        {
            storedRound =
                _reviewStore.GetWorkflowRound(round.Id)
                ?? throw new InvalidOperationException("Workflow round does not exist in the configured store.");
            ValidateRoundIdentity(storedRun, round, storedRound);
        }
        var instanceId = storedRound is null ? $"review-run-{storedRun.Id}" : $"review-round-{storedRound.Id}";
        var scopePath = Path.Combine(RunDirectory(instanceId), ScopeFileName);
        JsonObject scope;
        try
        {
            scope =
                JsonNode.Parse(await File.ReadAllTextAsync(scopePath, cancellationToken).ConfigureAwait(false))
                    as JsonObject
                ?? throw new JsonException("Workflow scope is not an object.");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidDataException("Workflow scope could not be read.", ex);
        }
        if (
            scope["ReviewRunId"]?.GetValue<long>() != storedRun.Id
            || scope["WorkflowInstanceId"]?.GetValue<string>() != instanceId
            || !JsonNode.DeepEquals(scope["Admission"], BuildAdmission(storedRun, storedRound))
            || scope["FrozenContext"] is not JsonObject frozen
        )
        {
            throw new InvalidDataException("Workflow scope does not match its durable admission.");
        }
        return (JsonObject)frozen.DeepClone();
    }

    /// <summary>Restores every unresolved workspace lease before fresh polling starts.</summary>
    public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) =>
        RecoverActiveWorkspacesAsync(cancellationToken, releaseSettled: true);

    public Task RecoverActiveOwnerAsync(ReviewRun run, CancellationToken cancellationToken) =>
        RecoverOwnersAsync([run], cancellationToken, releaseSettled: true);

    internal Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken, bool releaseSettled) =>
        RecoverOwnersAsync(_reviewStore.ListActiveWorkflowWorkspaceRuns(), cancellationToken, releaseSettled);

    private async Task RecoverOwnersAsync(
        IReadOnlyList<ReviewRun> active,
        CancellationToken cancellationToken,
        bool releaseSettled
    )
    {
        foreach (var run in active)
        {
            var assignment = _workspace.ReadAssignment(run);
            if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
                throw new InvalidDataException("Active workflow workspace assignment has no owner.");
            _ = await _workspace
                .RecoverAsync(run, assignment.AssignmentId, assignment.WorkflowInstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
        foreach (var run in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifact = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.AssignmentArtifactKind)!;
            var assignment =
                JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(artifact.Payload)
                ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
            if (assignment.Active)
            {
                if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
                {
                    throw new InvalidDataException("Active workflow workspace assignment has no owner.");
                }
                var owner = assignment.WorkflowInstanceId;
                try
                {
                    if (releaseSettled && _resetFailedAttempts)
                    {
                        if (
                            _reviewStore
                                .GetArtifacts(run.Id)
                                .Any(artifact =>
                                    artifact.ArtifactKind.StartsWith("workflow-session:", StringComparison.Ordinal)
                                    && JsonNode.Parse(artifact.Payload)?["InvocationId"] is null
                                )
                        )
                            throw new InvalidOperationException(
                                "Legacy hosted owner requires explicit inactive-tree recovery before automatic reset."
                            );
                        var round = ResolveRoundOwner(run, owner);
                        var snapshot = await _workflowStore.LoadAsync(owner, cancellationToken).ConfigureAwait(false);
                        if (
                            run.ParkedAt is null
                            && snapshot is not null
                            && (round is null || round.Kind == WorkflowRoundKind.Merged)
                        )
                        {
                            var inFlight = snapshot
                                .Tasks.Where(task => task.Status == WorkflowTaskStatus.InFlight)
                                .ToArray();
                            var failedContext = _reviewStore.TryGetLatestArtifact(
                                run.Id,
                                WorkflowWorkspace.PreparationDiagnosticArtifactKind
                            );
                            var failedCheckout = _reviewStore.TryGetLatestArtifact(
                                run.Id,
                                WorkflowWorkspace.CheckoutArtifactKind
                            );
                            var settledPreparation = _reviewStore.TryGetLatestArtifact(
                                run.Id,
                                WorkflowWorkspace.RemotePreparationArtifactKind
                            );
                            var terminalContextOwner =
                                round is { Kind: WorkflowRoundKind.Merged }
                                && round.ReviewRunId == run.Id
                                && round.WorkflowInstanceId == owner
                                && run.PrLifecycleState is PrLifecycleState.Merged or PrLifecycleState.Abandoned
                                && _reviewStore.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Merged)?.Id == round.Id
                                && _reviewStore.GetOutboxForRun(run.Id).Count == 0;
                            var contextCheckoutMatches = terminalContextOwner
                                ? failedCheckout is null
                                : round is null
                                    && failedCheckout is not null
                                    && settledPreparation is not null
                                    && failedCheckout.Id < settledPreparation.Id
                                    && JsonSerializer.Deserialize<PreparedCheckout>(failedCheckout.Payload)
                                        is { } checkout
                                    && checkout.StoreRoot == assignment.Slot.WorktreeRoot
                                    && checkout.TargetDir == assignment.Slot.SourceRoot
                                    && checkout.NotesDir == $"{assignment.Slot.WorktreeRoot}/PRs/{run.PrId}"
                                    && checkout.SourceHeadSha == run.HeadSha
                                    && checkout.TargetBaseSha == run.BaseSha
                                    && checkout.CheckoutSha is { Length: 40 } merge
                                    && merge.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
                            var settledContextFailure =
                                settledPreparation is not null
                                && JsonNode.Parse(settledPreparation.Payload) is JsonObject settledState
                                && settledState["AssignmentId"]?.GetValue<string>() == assignment.AssignmentId
                                && settledState["Settled"]?.GetValue<bool>() == true
                                && settledState["OutcomeUnknown"]?.GetValue<bool>() == false
                                && failedContext is not null
                                && contextCheckoutMatches
                                && settledPreparation.Id < failedContext.Id
                                && JsonNode.Parse(failedContext.Payload) is JsonObject contextFailure
                                && contextFailure["AssignmentId"]?.GetValue<string>() == assignment.AssignmentId
                                && contextFailure["Phase"]?.GetValue<string>() == "context-read"
                                && contextFailure["WriteUnacknowledged"]?.GetValue<bool>() == false
                                && contextFailure["Errors"] is JsonArray { Count: > 0 } errors
                                && errors[0]?["Type"]?.GetValue<string>() == nameof(IOException)
                                && _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.PreparationArtifactKind)
                                    is null
                                && !_reviewStore
                                    .GetArtifacts(run.Id)
                                    .Any(artifact =>
                                        artifact.ArtifactKind.StartsWith("workflow-session:", StringComparison.Ordinal)
                                    );
                            if (
                                inFlight.Length == 1
                                && (
                                    (round is null && inFlight[0].NodeId == "prepare-independent")
                                    || (
                                        terminalContextOwner
                                        && settledContextFailure
                                        && inFlight[0].NodeId == "prepare-history"
                                    )
                                )
                                && snapshot
                                    .Definition!.Nodes.OfType<ProceduralNode>()
                                    .SingleOrDefault(node => node.Id == inFlight[0].NodeId)
                                    ?.TaskList?.SingleOrDefault()
                                    is { Delegate: DelegateKind.Script, Script: { } script }
                                && Path.GetFileNameWithoutExtension(script)
                                    == (round is null ? "prepare-independent" : "prepare-history")
                                && _reviewStore.TryGetLatestArtifact(
                                    run.Id,
                                    WorkflowWorkspace.RemotePreparationArtifactKind
                                )
                                    is { } preparation
                                && (
                                    JsonNode.Parse(preparation.Payload)?["OutcomeUnknown"]?.GetValue<bool>() == true
                                    || _workspace.HasAcceptedPreparationCleanup(run, owner)
                                    || settledContextFailure
                                )
                            )
                            {
                                if (settledContextFailure && preparation.Id != settledPreparation!.Id)
                                    throw new InvalidOperationException(
                                        "Failed context preparation changed before selected-attempt reset."
                                    );
                                ValidateSnapshot(snapshot, owner, BuildAdmission(run, round));
                                await _workspace
                                    .ResetSettledAttemptAsync(run, owner, cancellationToken)
                                    .ConfigureAwait(false);
                                var reset =
                                    _reviewStore.TryGetLatestArtifact(run.Id, "workflow-attempt-reset")
                                    ?? throw new InvalidDataException(
                                        "Preparation abandonment has no cleanup receipt."
                                    );
                                if (
                                    settledContextFailure
                                    && (
                                        _workspace.ReadAssignment(run) != assignment
                                        || _reviewStore
                                            .TryGetLatestArtifact(
                                                run.Id,
                                                WorkflowWorkspace.RemotePreparationArtifactKind
                                            )
                                            ?.Id != settledPreparation!.Id
                                        || _reviewStore
                                            .TryGetLatestArtifact(
                                                run.Id,
                                                WorkflowWorkspace.PreparationDiagnosticArtifactKind
                                            )
                                            ?.Id != failedContext!.Id
                                        || _reviewStore
                                            .TryGetLatestArtifact(run.Id, WorkflowWorkspace.CheckoutArtifactKind)
                                            ?.Id != failedCheckout?.Id
                                        || (terminalContextOwner && _reviewStore.GetOutboxForRun(run.Id).Count != 0)
                                        || _reviewStore.TryGetLatestArtifact(
                                            run.Id,
                                            WorkflowWorkspace.PreparationArtifactKind
                                        )
                                            is not null
                                        || _reviewStore
                                            .GetArtifacts(run.Id)
                                            .Any(artifact =>
                                                artifact.ArtifactKind.StartsWith(
                                                    "workflow-session:",
                                                    StringComparison.Ordinal
                                                )
                                            )
                                    )
                                )
                                    throw new InvalidOperationException(
                                        "Failed context preparation changed during selected-attempt reset."
                                    );
                                _reviewStore.AddArtifact(
                                    new ReviewArtifact
                                    {
                                        ReviewRunId = run.Id,
                                        ArtifactKind = "workflow-preparation-reset-disposition",
                                        ArtifactSchemaVersion = 1,
                                        Provider = "workflow",
                                        Payload = new JsonObject
                                        {
                                            ["WorkflowInstanceId"] = owner,
                                            ["AssignmentId"] = assignment.AssignmentId,
                                            ["LatestPreparationArtifactId"] = preparation.Id,
                                            ["ResetArtifactId"] = reset.Id,
                                            ["OriginalTask"] = JsonSerializer.SerializeToNode(inFlight[0]),
                                            ["Disposition"] = settledContextFailure
                                                ? "Failed context preparation abandoned after verified cleanup."
                                                : "Failed after verified cleanup; original checkout outcome remains unknown.",
                                        }.ToJsonString(),
                                    }
                                );
                                snapshot = snapshot with
                                {
                                    Tasks = snapshot
                                        .Tasks.Select(task =>
                                            task == inFlight[0]
                                                ? task with
                                                {
                                                    Status = WorkflowTaskStatus.Failed,
                                                    LastError =
                                                        "Preparation attempt abandoned after verified selected-slot reset.",
                                                }
                                                : task
                                        )
                                        .ToArray(),
                                };
                                await _workflowStore
                                    .SaveAsync(owner, snapshot, cancellationToken)
                                    .ConfigureAwait(false);
                                inFlight = [];
                            }
                            if (
                                inFlight.Length == 1
                                && snapshot
                                    .Definition!.Nodes.OfType<ProceduralNode>()
                                    .SingleOrDefault(node => node.Id == inFlight[0].NodeId)
                                    ?.TaskList?.SingleOrDefault()
                                    ?.Delegate == DelegateKind.Agent
                                && snapshot.Deadlines.TryGetValue(inFlight[0].Name, out var deadline)
                                && deadline <= DateTimeOffset.UtcNow
                            )
                            {
                                _reconcileOnly.Value = true;
                                try
                                {
                                    _ = await ResumeAsync(run, round, cancellationToken).ConfigureAwait(false);
                                }
                                finally
                                {
                                    _reconcileOnly.Value = false;
                                }
                                if (!_workspace.HasActiveAssignment(run, owner))
                                    continue;
                                snapshot = await _workflowStore
                                    .LoadAsync(owner, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        if (
                            run.ParkedAt is null
                            && (round is null || round.Kind == WorkflowRoundKind.Merged)
                            && snapshot is not null
                            && IsSettled(snapshot)
                            && (
                                snapshot.IsComplete
                                || snapshot.Tasks.Any(task => task.Status == WorkflowTaskStatus.Failed)
                            )
                        )
                        {
                            ValidateSnapshot(snapshot, owner, BuildAdmission(run, round));
                            var invoker = await _invokerFactory(run, owner, RunDirectory(owner), cancellationToken)
                                .ConfigureAwait(false);
                            var pending = invoker is ReviewWorkflowInvoker hosted
                                ? await hosted
                                    .SettleCompletedSessionsAsync(
                                        cancellationToken,
                                        snapshot: snapshot,
                                        includeFailed: true
                                    )
                                    .ConfigureAwait(false)
                                : null;
                            if (pending is null)
                            {
                                await _workspace
                                    .ResetSettledAttemptAsync(run, owner, cancellationToken)
                                    .ConfigureAwait(false);
                                if (snapshot.IsComplete)
                                    CompleteAdmission(run, round, owner);
                            }
                        }
                    }
                    if (
                        releaseSettled
                        && await IsOwnerDurablySettledAsync(run, owner, cancellationToken).ConfigureAwait(false)
                    )
                    {
                        await _workspace
                            .ReleaseOrRetireAsync(run.Id, owner, workSettled: true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(
                        "Workflow recovery held {RunId} {PrId} {WorkflowInstanceId} after {ErrorType}; unrelated slots continue.",
                        run.Id,
                        run.PrId,
                        owner,
                        ex.GetType().Name
                    );
                    _reviewStore.AddArtifact(
                        new ReviewArtifact
                        {
                            ReviewRunId = run.Id,
                            ArtifactKind = "workflow-recovery-pending",
                            ArtifactSchemaVersion = 1,
                            Provider = "workflow",
                            Payload = new JsonObject
                            {
                                ["WorkflowInstanceId"] = owner,
                                ["AssignmentId"] = assignment.AssignmentId,
                                ["ErrorType"] = ex.GetType().Name,
                                ["Disposition"] = "held; unrelated slots may continue",
                            }.ToJsonString(),
                        }
                    );
                }
            }
        }
    }

    /// <summary>Reconciles the exact workflow instance that owns this run's active workspace.</summary>
    public async Task<WorkflowInvocationStatus?> ReconcileActiveOwnerAsync(
        ReviewRun run,
        string requestedWorkflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        var artifact = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.AssignmentArtifactKind);
        if (artifact is null)
        {
            return null;
        }
        var assignment =
            JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(artifact.Payload)
            ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
        if (
            !assignment.Active
            || string.Equals(assignment.WorkflowInstanceId, requestedWorkflowInstanceId, StringComparison.Ordinal)
        )
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
        {
            throw new InvalidDataException("Active workflow workspace assignment has no owner.");
        }
        var ownerRound = ResolveRoundOwner(run, assignment.WorkflowInstanceId);
        return await ResumeAsync(run, ownerRound, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReleaseSettledAssignmentIfActiveAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        var artifact = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.AssignmentArtifactKind);
        if (artifact is null)
        {
            return;
        }
        var assignment =
            JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(artifact.Payload)
            ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
        if (!assignment.Active)
        {
            return;
        }
        if (!string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
        {
            return;
        }
        var owner = assignment.WorkflowInstanceId;
        _ = await _workspace.RecoverAsync(run, assignment.AssignmentId, owner, cancellationToken).ConfigureAwait(false);
        await _workspace
            .ReleaseOrRetireAsync(run.Id, owner, workSettled: true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<bool> IsOwnerDurablySettledAsync(
        ReviewRun run,
        string instanceId,
        CancellationToken cancellationToken
    )
    {
        if (run.ParkedAt is not null)
            return false;
        var ownedRound = ResolveRoundOwner(run, instanceId);
        if (_resetFailedAttempts && (ownedRound is null || ownedRound.Kind == WorkflowRoundKind.Merged))
        {
            var snapshot = await _workflowStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (
                snapshot?.Tasks.Any(task => task.Status == WorkflowTaskStatus.Failed) == true
                || snapshot?.IsComplete == true
            )
            {
                var savedReset = _reviewStore.TryGetLatestArtifact(run.Id, "workflow-attempt-reset");
                if (savedReset is null)
                    return false;
                var receipt = JsonNode.Parse(savedReset.Payload)!;
                var assignment = _workspace.ReadAssignment(run);
                if (
                    receipt["status"]?.GetValue<string>() != "reset"
                    || receipt["identity"]?["runId"]?.GetValue<long>() != run.Id
                    || receipt["identity"]?["assignmentId"]?.GetValue<string>() != assignment.AssignmentId
                    || receipt["identity"]?["workflowInstanceId"]?.GetValue<string>() != instanceId
                    || receipt["identity"]?["head"]?.GetValue<string>() != run.HeadSha
                    || receipt["identity"]?["base"]?.GetValue<string>() != run.BaseSha
                )
                    return false;
            }
        }
        if (string.Equals(instanceId, InstanceIdFor(run, null), StringComparison.Ordinal))
        {
            return await IsDurablySettledAsync(instanceId, BuildAdmission(run, null), cancellationToken)
                .ConfigureAwait(false);
        }
        const string prefix = "review-round-";
        if (
            !instanceId.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(instanceId.AsSpan(prefix.Length), out var roundId)
        )
        {
            return false;
        }
        var round = _reviewStore.GetWorkflowRound(roundId);
        return round is not null
            && await IsDurablySettledAsync(instanceId, BuildAdmission(run, round), cancellationToken)
                .ConfigureAwait(false);
    }

    private WorkflowRound? ResolveRoundOwner(ReviewRun run, string instanceId)
    {
        if (string.Equals(instanceId, InstanceIdFor(run, null), StringComparison.Ordinal))
        {
            return null;
        }
        const string prefix = "review-round-";
        if (
            !instanceId.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(instanceId.AsSpan(prefix.Length), out var roundId)
        )
        {
            throw new InvalidDataException("Active workflow workspace assignment has an invalid owner.");
        }
        var round =
            _reviewStore.GetWorkflowRound(roundId)
            ?? throw new InvalidDataException("Active workflow workspace owner round does not exist.");
        ValidateRoundIdentity(run, round, round);
        return round;
    }

    private async Task<bool> IsDurablySettledAsync(
        string instanceId,
        JsonObject admission,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var snapshot = await _workflowStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return false;
            }
            ValidateSnapshot(snapshot, instanceId, admission);
            return IsSettled(snapshot);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private ReviewRun ReadAndValidateRun(ReviewRun run)
    {
        if (run.Id <= 0)
        {
            throw new ArgumentException("Review run must be persisted before workflow admission.", nameof(run));
        }
        var stored =
            _reviewStore.GetReviewRun(run.Id)
            ?? throw new InvalidOperationException("Review run does not exist in the configured store.");
        if (stored.ParkedAt is not null)
            throw new InvalidOperationException("Parked review runs require operator reconciliation before admission.");
        if (
            stored.RepoId != run.RepoId
            || !string.Equals(stored.PrId, run.PrId, StringComparison.Ordinal)
            || !string.Equals(stored.HeadSha, run.HeadSha, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Review run identity does not match the configured store.");
        }
        return stored;
    }

    private WorkflowRound? ReadAndValidateRound(ReviewRun run, WorkflowRound? round, JsonObject frozenContext)
    {
        if (round is null)
        {
            return null;
        }
        var stored =
            _reviewStore.GetWorkflowRound(round.Id)
            ?? throw new InvalidOperationException("Workflow round does not exist in the configured store.");
        ValidateRoundIdentity(run, round, stored);
        if (stored.CompletedAt is not null || stored.Outcome == WorkflowRoundOutcome.OperatorReset)
            throw new InvalidOperationException("Terminal workflow round cannot be replayed by direct admission.");
        JsonObject storedContext;
        try
        {
            storedContext =
                JsonNode.Parse(stored.FrozenInputJson)?.AsObject() ?? throw new JsonException("Frozen input is null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Workflow round has invalid frozen context.", ex);
        }
        if (!JsonNode.DeepEquals(storedContext, frozenContext))
        {
            throw new InvalidDataException("Workflow round frozen context does not match its durable admission.");
        }
        return stored;
    }

    private static void ValidateRoundIdentity(ReviewRun run, WorkflowRound round, WorkflowRound stored)
    {
        if (
            stored.ReviewRunId != run.Id
            || round.ReviewRunId != run.Id
            || stored.RepoId != run.RepoId
            || !string.Equals(stored.PrId, run.PrId, StringComparison.Ordinal)
            || !string.Equals(stored.HeadSha, run.HeadSha, StringComparison.Ordinal)
            || stored.Kind != round.Kind
            || !string.Equals(stored.EventKey, round.EventKey, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Workflow round identity does not match its review run.");
        }
    }

    private static JsonObject BuildAdmission(ReviewRun run, WorkflowRound? round) =>
        new()
        {
            ["Route"] = round?.Kind switch
            {
                null => "new_head",
                WorkflowRoundKind.Discussion => "discussion",
                WorkflowRoundKind.Merged => run.PrLifecycleState == PrLifecycleState.Abandoned ? "abandoned" : "merged",
                _ => throw new InvalidOperationException("Unsupported workflow round kind."),
            },
            ["PrId"] = run.PrId,
            ["HeadSha"] = run.HeadSha,
            ["WindowId"] = round?.EventKey ?? string.Empty,
        };

    private WorkflowRuntime CreateRuntime(
        JsonObject admission,
        string admittedWorkflow,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var yaml = File.ReadAllText(admittedWorkflow);
        var definition = SimpleWorkflow.DeserializeYaml(yaml).ToDefinition() with
        {
            Inputs = (JsonObject)admission.DeepClone(),
        };
        var runtime = WorkflowRuntime.CreateNew();
        runtime.LoadDefinition(definition);
        return runtime;
    }

    private static WorkflowRuntime RestoreRuntime(
        WorkflowInstanceSnapshot snapshot,
        string instanceId,
        JsonObject admission
    )
    {
        ValidateSnapshot(snapshot, instanceId, admission);
        return WorkflowRuntime.FromSnapshot(snapshot);
    }

    private void ApplySessionBindings(WorkflowRuntime runtime, ReviewRun run, WorkflowRound? round)
    {
        if (_hostSessionBindings?.Invoke(run, round) is not { } bindings)
        {
            return;
        }
        foreach (var (name, sessionId) in bindings)
        {
            runtime.BindAutomaticSession(name, sessionId);
        }
    }

    private async Task<bool> AcquireOrRecoverAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        var prior = _reviewStore.TryGetLatestArtifact(run.Id, WorkflowWorkspace.AssignmentArtifactKind);
        if (_streamClaims.TryGetValue(run.Id, out var claim))
            return await _workspace
                .AdoptClaimAsync(run, workflowInstanceId, claim, cancellationToken)
                .ConfigureAwait(false);
        if (prior is null)
            return await _workspace.TryAcquireAsync(run, workflowInstanceId, cancellationToken).ConfigureAwait(false)
                is not null;
        var assignment =
            JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(prior.Payload)
            ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
        if (assignment.Active)
        {
            if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
            {
                throw new InvalidDataException("Active workflow workspace assignment has no owner.");
            }
            if (!string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
            {
                return false;
            }
            _ = await _workspace
                .RecoverAsync(run, assignment.AssignmentId, workflowInstanceId, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        return await _workspace.TryAcquireAsync(run, workflowInstanceId, cancellationToken).ConfigureAwait(false)
            is not null;
    }

    private static string InstanceIdFor(ReviewRun run, WorkflowRound? round) =>
        round is null ? $"review-run-{run.Id}" : $"review-round-{round.Id}";

    private void CompleteAdmission(ReviewRun run, WorkflowRound? round, string instanceId)
    {
        if (round is not null)
        {
            if (!_reviewStore.RecordWorkflowOutcome(round.Id, instanceId, WorkflowRoundOutcome.Succeeded))
            {
                throw new InvalidOperationException("Workflow round completion could not be persisted.");
            }
            return;
        }
        _reviewStore.UpdateReviewRunState(run.Id, ReviewStage.Posted, WorkflowStatus.Completed, run.PrLifecycleState);
    }

    private async Task<bool> HasDurablySettledSnapshotAsync(string instanceId, JsonObject admission)
    {
        try
        {
            var snapshot = await _workflowStore.LoadAsync(instanceId, CancellationToken.None).ConfigureAwait(false);
            if (snapshot is null)
            {
                return false;
            }
            ValidateSnapshot(snapshot, instanceId, admission);
            return IsSettled(snapshot);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSettled(WorkflowInstanceSnapshot snapshot) =>
        snapshot.Tasks.All(value => value.Status != WorkflowTaskStatus.InFlight);

    private static void ValidateSnapshot(WorkflowInstanceSnapshot snapshot, string instanceId, JsonObject admission)
    {
        if (!string.Equals(snapshot.InstanceId, instanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Workflow snapshot identity does not match its admission.");
        }
        if (snapshot.Definition?.StrictContracts != true)
        {
            throw new InvalidDataException("Review workflow snapshot does not contain strict contracts.");
        }
        if (!JsonNode.DeepEquals(snapshot.Inputs, admission))
        {
            throw new InvalidDataException("Workflow snapshot inputs do not match its admission.");
        }
    }

    private string RunDirectory(string instanceId)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)));
        var directory = Path.GetFullPath(name, _runDirectoryRoot);
        var rootWithSeparator =
            _runDirectoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!directory.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException("Workflow run directory escaped its configured root.");
        }
        return directory;
    }

    private static async Task WriteOrVerifyScopeAsync(
        string runDirectory,
        long reviewRunId,
        string instanceId,
        JsonObject admission,
        JsonObject frozenContext,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(runDirectory);
        var expected = new JsonObject
        {
            ["ReviewRunId"] = reviewRunId,
            ["WorkflowInstanceId"] = instanceId,
            ["Admission"] = admission.DeepClone(),
            ["FrozenContext"] = frozenContext.DeepClone(),
        };
        var scopePath = Path.Combine(runDirectory, ScopeFileName);
        if (File.Exists(scopePath))
        {
            await VerifyScopeAsync(scopePath, expected, cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporary = Path.Combine(runDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(expected.ToJsonString());
            await using (
                var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough
                )
            )
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporary, scopePath);
            }
            catch (IOException) when (File.Exists(scopePath))
            {
                await VerifyScopeAsync(scopePath, expected, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task VerifyScopeAsync(
        string scopePath,
        JsonObject expected,
        CancellationToken cancellationToken
    )
    {
        JsonNode? actual;
        try
        {
            actual = JsonNode.Parse(await File.ReadAllTextAsync(scopePath, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Existing workflow scope is invalid JSON.", ex);
        }
        if (!JsonNode.DeepEquals(actual, expected))
        {
            throw new InvalidDataException("Existing workflow scope does not match its immutable admission.");
        }
    }
}
