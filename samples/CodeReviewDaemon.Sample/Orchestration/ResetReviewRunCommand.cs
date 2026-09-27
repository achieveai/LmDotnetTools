using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Operator-only retirement of one incomplete, prepared collect-only review.</summary>
internal static class ResetReviewRunCommand
{
    private const string IntentKind = "workflow-slot-reset-intent";
    private const string ReceiptKind = "workflow-slot-reset-receipt";

    public static async Task<JsonObject> RunAsync(
        long runId,
        bool confirm,
        string databasePath,
        CodeReviewDaemonOptions options,
        ReviewStore store,
        WorkflowWorkspace workspace,
        IWorkflowStore workflows,
        IReviewSessionProvisioner sessions,
        LmStreamingS2SClient client,
        ILoggerFactory loggerFactory,
        CancellationToken ct
    )
    {
        if (options.EnablePrPolling || options.EnableCommentPosting || options.EnableGitPush)
            throw new InvalidOperationException("Reset requires polling and broad publication gates disabled.");
        using var lease = RunPrCoordinatorLease.TryAcquire(databasePath);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Stop/drain the daemon before resetting a review slot.");
        var run = store.GetReviewRun(runId) ?? throw new InvalidOperationException("Review run is missing.");
        if (run.WorkflowStatus == WorkflowStatus.Completed || run.Mode != "collect-only")
            throw new InvalidOperationException("Reset supports incomplete collect-only runs only.");
        var assignment = Read<WorkflowWorkspaceAssignment>(WorkflowWorkspace.AssignmentArtifactKind);
        assignment.Slot.Validate();
        if (assignment.WorkflowInstanceId != $"review-run-{run.Id}")
            throw new InvalidOperationException("Reset of discussion-round assignments is not supported.");
        if (store.TryGetLatestArtifact(runId, WorkflowWorkspace.PreparationArtifactKind) is null)
            return await ResetFailedPreparationAsync(
                    run,
                    assignment,
                    confirm,
                    store,
                    workspace,
                    workflows,
                    sessions,
                    ct
                )
                .ConfigureAwait(false);
        if (!assignment.Active && store.TryGetLatestArtifact(runId, ReceiptKind) is not null)
        {
            var prior = Read<JsonObject>(ReceiptKind);
            var priorPrepared = Read<WorkflowWorkspacePreparation>(WorkflowWorkspace.PreparationArtifactKind);
            if (
                prior["schema"]?.GetValue<int>() != 1
                || prior["status"]?.GetValue<string>() != "reset"
                || priorPrepared.AssignmentId != assignment.AssignmentId
                || priorPrepared.Slot != assignment.Slot
                || prior["identity"]?["assignmentId"]?.GetValue<string>() != assignment.AssignmentId
                || prior["identity"]?["runId"]?.GetValue<long>() != run.Id
                || prior["identity"]?["workflowInstanceId"]?.GetValue<string>() != assignment.WorkflowInstanceId
                || prior["identity"]?["repository"]?.GetValue<string>() != assignment.Slot.RepositoryName
                || prior["identity"]?["slot"]?.GetValue<int>() != assignment.Slot.Index
                || prior["identity"]?["pr"]?.GetValue<string>() != run.PrId
                || prior["identity"]?["head"]?.GetValue<string>() != run.HeadSha
                || prior["identity"]?["base"]?.GetValue<string>() != run.BaseSha
                || prior["identity"]?["merge"]?.GetValue<string>() != priorPrepared.Checkout.CheckoutSha
                || prior["identity"]?["sourcePath"]?.GetValue<string>() != assignment.Slot.SourceRelativePath
            )
                throw new InvalidOperationException("Inactive assignment has no matching reset receipt.");
            return new JsonObject { ["status"] = "already_reset", ["runId"] = runId };
        }
        if (
            WorkflowWorkspace.HasAnyUnsettledPreparation(store)
            || store
                .GetOutboxForRun(runId)
                .Any(row => row.Status is not (OutboxStatus.Posted or OutboxStatus.Sent or OutboxStatus.Collected))
            || store
                .ListActiveWorkflowWorkspaceRuns()
                .Where(other => other.Id != runId)
                .Any(other =>
                    JsonSerializer
                        .Deserialize<WorkflowWorkspaceAssignment>(
                            store.TryGetLatestArtifact(other.Id, WorkflowWorkspace.AssignmentArtifactKind)!.Payload
                        )
                        ?.Slot == assignment.Slot
                )
        )
            throw new InvalidOperationException(
                "Preparation, publication, or conflicting slot ownership is unresolved."
            );
        var prepared = Read<WorkflowWorkspacePreparation>(WorkflowWorkspace.PreparationArtifactKind);
        if (
            prepared.AssignmentId != assignment.AssignmentId
            || prepared.Slot != assignment.Slot
            || prepared.Checkout.SourceHeadSha != run.HeadSha
            || prepared.Checkout.TargetBaseSha != run.BaseSha
            || string.IsNullOrWhiteSpace(prepared.Checkout.CheckoutSha)
        )
            throw new InvalidOperationException("Prepared checkout does not match the frozen review identity.");
        var snapshot =
            await workflows.LoadAsync(assignment.WorkflowInstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Frozen workflow is missing.");
        await RequireSettledAsync(snapshot, run, prepared, store, client, loggerFactory, options, databasePath, ct)
            .ConfigureAwait(false);
        var identity = new JsonObject
        {
            ["runId"] = runId,
            ["assignmentId"] = assignment.AssignmentId,
            ["workflowInstanceId"] = assignment.WorkflowInstanceId,
            ["repository"] = assignment.Slot.RepositoryName,
            ["slot"] = assignment.Slot.Index,
            ["pr"] = run.PrId,
            ["head"] = run.HeadSha,
            ["base"] = run.BaseSha,
            ["merge"] = prepared.Checkout.CheckoutSha,
            ["sourcePath"] = assignment.Slot.SourceRelativePath,
        };
        var priorIntent = store.TryGetLatestArtifact(runId, IntentKind);
        if (priorIntent is not null && !JsonNode.DeepEquals(JsonNode.Parse(priorIntent.Payload), identity))
            throw new InvalidOperationException("Reset intent belongs to a different assignment.");
        if (run.ParkedAt is not null && priorIntent is null)
            throw new InvalidOperationException("Run was parked for another reason; reset refused.");
        var session = await sessions.GetOrCreateSharedAsync(ct).ConfigureAwait(false);
        var helper = new ReviewSetupScriptRunner(session.CommandRunner, session.FileSystem, false);
        var inspection = await helper.ResetReviewSlotAsync(identity, apply: false, ct).ConfigureAwait(false);
        if (!confirm)
            return inspection;
        await RequireSettledAsync(snapshot, run, prepared, store, client, loggerFactory, options, databasePath, ct)
            .ConfigureAwait(false);
        if (priorIntent is null)
            Save(IntentKind, identity);
        store.TryMarkReviewRunParked(
            runId,
            DateTimeOffset.UtcNow,
            "Explicit operator slot reset; historical review preserved."
        );
        var receipt = await helper.ResetReviewSlotAsync(identity, apply: true, ct).ConfigureAwait(false);
        Save(ReceiptKind, receipt);
        if (assignment.Active)
            workspace.InvalidateReconciledAssignment(run, assignment.AssignmentId);
        return receipt;

        T Read<T>(string kind) =>
            JsonSerializer.Deserialize<T>(
                store.TryGetLatestArtifact(runId, kind)?.Payload
                    ?? throw new InvalidOperationException($"Required reset evidence is missing: {kind}.")
            ) ?? throw new InvalidDataException("Invalid reset evidence.");

        void Save(string kind, JsonObject value) =>
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = runId,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = 1,
                    Provider = "operator",
                    Payload = value.ToJsonString(),
                }
            );
    }

    private static async Task<JsonObject> ResetFailedPreparationAsync(
        ReviewRun run,
        WorkflowWorkspaceAssignment assignment,
        bool confirm,
        ReviewStore store,
        WorkflowWorkspace workspace,
        IWorkflowStore workflows,
        IReviewSessionProvisioner sessions,
        CancellationToken ct
    )
    {
        JsonObject Read(string kind) =>
            JsonNode
                .Parse(
                    store.TryGetLatestArtifact(run.Id, kind)?.Payload
                        ?? throw new InvalidOperationException($"Missing reset evidence: {kind}.")
                )!
                .AsObject();
        void Save(string kind, JsonObject value) =>
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = 1,
                    Provider = "operator",
                    Payload = value.ToJsonString(),
                }
            );
        if (
            assignment.Active
            || store.ListActiveWorkflowWorkspaceRuns().Count != 0
            || store.HasUnsettledWorkspaceBootstrap()
            || store.GetOutboxForRun(run.Id).Count != 0
            || run.WorkflowStatus != WorkflowStatus.RetryPending
            || run.Stage != ReviewStage.Discovered
            || run.ParkedAt is not null
            || store
                .ListReviewRunsWithArtifact(WorkflowWorkspace.RemotePreparationArtifactKind)
                .Where(other => other.Id != run.Id)
                .Any(other =>
                {
                    var value = JsonNode.Parse(
                        store.TryGetLatestArtifact(other.Id, WorkflowWorkspace.RemotePreparationArtifactKind)!.Payload
                    )!;
                    return value["Settled"]?.GetValue<bool>() != true
                        || value["OutcomeUnknown"]?.GetValue<bool>() == true;
                })
        )
            throw new InvalidOperationException(
                "Failed preparation reset requires exclusive inactive ownership and no publication."
            );
        var state = Read(WorkflowWorkspace.RemotePreparationArtifactKind);
        if (state["AssignmentId"]?.GetValue<string>() != assignment.AssignmentId)
            throw new InvalidDataException("Preparation intent belongs to another assignment.");
        var owner = state["OwnerProcessId"]?.GetValue<int>() ?? 0;
        var started = state["OwnerStartTimeUtcTicks"]?.GetValue<long>() ?? 0;
        if (owner <= 0 || started <= 0)
            throw new InvalidDataException("Preparation owner identity is missing.");
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(owner);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == started)
                throw new InvalidOperationException("Original preparation process is still running.");
        }
        catch (ArgumentException) { }
        var snapshot =
            await workflows.LoadAsync(assignment.WorkflowInstanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Frozen workflow is missing.");
        var task = snapshot.Tasks.SingleOrDefault();
        if (
            snapshot.IsComplete
            || snapshot.CurrentNodeId != "prepare-independent"
            || task is null
            || task.NodeId != "prepare-independent"
            || task.Attempts != 0
            || task.Status is not (WorkflowTaskStatus.Failed or WorkflowTaskStatus.Pending)
            || snapshot.Sessions.Any(pair => pair.Key != "review-parent" || pair.Value != $"review-parent-{run.Id}")
            || store
                .GetArtifacts(run.Id)
                .Any(artifact => artifact.ArtifactKind.StartsWith("workflow-session:", StringComparison.Ordinal))
        )
            throw new InvalidOperationException("Only failed preparation before any reviewer can be reset here.");
        var prior = store.TryGetLatestArtifact(run.Id, ReceiptKind);
        if (
            task.Status != WorkflowTaskStatus.Failed
            && (
                prior is null
                || task.ToolCallId?.EndsWith(":operator-reset:" + assignment.AssignmentId, StringComparison.Ordinal)
                    != true
            )
        )
            throw new InvalidOperationException("Preparation has not failed or been explicitly reset.");
        var checkout = JsonSerializer.Deserialize<PreparedCheckout>(
            store.TryGetLatestArtifact(run.Id, WorkflowWorkspace.CheckoutArtifactKind)?.Payload
                ?? throw new InvalidOperationException("Exact prepared checkout is missing.")
        )!;
        if (
            checkout.SourceHeadSha != run.HeadSha
            || checkout.TargetBaseSha != run.BaseSha
            || checkout.TargetDir != assignment.Slot.SourceRoot
            || checkout.StoreRoot != assignment.Slot.WorktreeRoot
            || checkout.NotesDir != assignment.Slot.WorktreeRoot + "/PRs/" + run.PrId
        )
            throw new InvalidDataException("Failed checkout does not match the assigned review.");
        var identity = new JsonObject
        {
            ["runId"] = run.Id,
            ["assignmentId"] = assignment.AssignmentId,
            ["workflowInstanceId"] = assignment.WorkflowInstanceId,
            ["repository"] = assignment.Slot.RepositoryName,
            ["slot"] = assignment.Slot.Index,
            ["pr"] = run.PrId,
            ["head"] = run.HeadSha,
            ["base"] = run.BaseSha,
            ["merge"] = checkout.CheckoutSha,
            ["sourcePath"] = assignment.Slot.SourceRelativePath,
        };
        var session = await sessions.GetOrCreateSharedAsync(ct).ConfigureAwait(false);
        var helper = new ReviewSetupScriptRunner(session.CommandRunner, session.FileSystem, false);
        var inspection = await helper.ResetReviewSlotAsync(identity, apply: false, ct).ConfigureAwait(false);
        if (!confirm)
            return inspection;
        if (
            store.TryGetLatestArtifact(run.Id, IntentKind) is { } intent
            && !JsonNode.DeepEquals(JsonNode.Parse(intent.Payload), identity)
        )
            throw new InvalidOperationException("Reset intent belongs to a different assignment.");
        if (prior is not null && !JsonNode.DeepEquals(JsonNode.Parse(prior.Payload)?["identity"], identity))
            throw new InvalidOperationException("Reset receipt belongs to a different assignment.");
        Save(IntentKind, identity);
        var receipt = await helper.ResetReviewSlotAsync(identity, apply: true, ct).ConfigureAwait(false);
        if (receipt["status"]?.GetValue<string>() != "reset" || !JsonNode.DeepEquals(receipt["identity"], identity))
            throw new InvalidDataException("Remote reset did not confirm the exact assignment.");
        Save(ReceiptKind, receipt);
        Save(
            "workflow-preparation-reset-acceptance",
            new JsonObject
            {
                ["AssignmentId"] = assignment.AssignmentId,
                ["OriginalTask"] = JsonSerializer.SerializeToNode(task),
                ["OriginalDeadline"] = snapshot.Deadlines.TryGetValue(task.Name, out var deadline)
                    ? JsonSerializer.SerializeToNode(deadline)
                    : null,
                ["OriginalOutcomeUnknown"] = state["OutcomeUnknown"]?.DeepClone(),
                ["Basis"] = "Explicit operator reset of failed preparation; original write outcome not reconstructed.",
            }
        );
        var retryId =
            (task.ToolCallId ?? $"{snapshot.InstanceId}/{task.Name}/{task.Attempts}")
            + ":operator-reset:"
            + assignment.AssignmentId;
        if (task.Status == WorkflowTaskStatus.Failed)
            await workflows
                .SaveAsync(
                    snapshot.InstanceId,
                    snapshot with
                    {
                        Tasks =
                        [
                            task with
                            {
                                Status = WorkflowTaskStatus.Pending,
                                ToolCallId = retryId,
                                LastError = null,
                            },
                        ],
                        Deadlines = snapshot
                            .Deadlines.Where(pair => pair.Key != task.Name)
                            .ToDictionary(pair => pair.Key, pair => pair.Value),
                    },
                    ct
                )
                .ConfigureAwait(false);
        workspace.CompleteOperatorPreparationReset(run, assignment.AssignmentId);
        return receipt;
    }

    private static async Task RequireSettledAsync(
        WorkflowInstanceSnapshot snapshot,
        ReviewRun run,
        WorkflowWorkspacePreparation prepared,
        ReviewStore store,
        LmStreamingS2SClient client,
        ILoggerFactory loggerFactory,
        CodeReviewDaemonOptions options,
        string databasePath,
        CancellationToken ct
    )
    {
        if (snapshot.InstanceId != $"review-run-{run.Id}" || snapshot.SchemaVersion != 2 || snapshot.Definition is null)
            throw new InvalidDataException("Unsupported frozen workflow identity.");
        var threads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in snapshot.Tasks.Where(task => task.Status != WorkflowTaskStatus.Pending))
        {
            var node = snapshot.Definition.Nodes.OfType<ProceduralNode>().Single(n => n.Id == occurrence.NodeId);
            var task = node.TaskList!.Single(t => t.Id == occurrence.TaskId);
            if (task.Delegate != DelegateKind.Agent)
            {
                if (
                    occurrence.Status != WorkflowTaskStatus.Validated
                    && !(
                        occurrence.Status == WorkflowTaskStatus.Failed
                        && occurrence.NodeId == "fetch-discussion"
                        && task.Delegate == DelegateKind.Script
                        && Path.GetFileNameWithoutExtension(task.Script) == "fetch-discussion"
                    )
                )
                    throw new InvalidOperationException("A script invocation remains unresolved.");
                if (occurrence.Status == WorkflowTaskStatus.Failed)
                {
                    var invocationId =
                        occurrence.ToolCallId ?? $"{snapshot.InstanceId}/{occurrence.Name}/{occurrence.Attempts}";
                    var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.InstanceId)));
                    var invocationHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocationId)));
                    var receiptPath = Path.Combine(
                        Path.GetFullPath(options.WorkflowStateDirectory ?? databasePath + ".workflow"),
                        "runs",
                        runHash,
                        "private",
                        "invocation-" + invocationHash + ".json"
                    );
                    if (new FileInfo(receiptPath).Length > checked(options.Limits.MaxArtifactPayloadChars * 4L))
                        throw new InvalidDataException("Failed read receipt exceeds its bound.");
                    var evidence = JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, ct).ConfigureAwait(false));
                    if (
                        occurrence.Attempts != 0
                        || snapshot.CurrentNodeId != "fetch-discussion"
                        || snapshot.Tasks.Any(t =>
                            t != occurrence && t.Status is WorkflowTaskStatus.InFlight or WorkflowTaskStatus.Failed
                        )
                        || evidence?["SchemaVersion"]?.GetValue<int>() != 1
                        || evidence["Invocation"]?["Attempt"]?.GetValue<int>() != 1
                        || evidence["Invocation"]?["IsCorrection"]?.GetValue<bool>() != false
                        || new[] { "independent-review", "independent-grade", "collect-comments" }.Any(id =>
                            !snapshot.Tasks.Any(t => t.NodeId == id && t.Status == WorkflowTaskStatus.Validated)
                        )
                        || task.Id != "fetch-discussion:task"
                        || store.GetOutboxForRun(run.Id).Any()
                        || evidence?["Result"]?["Status"]?.GetValue<int>() != (int)WorkflowInvocationStatus.Failed
                        || evidence["Invocation"]?["InstanceId"]?.GetValue<string>() != snapshot.InstanceId
                        || evidence["Invocation"]?["InvocationId"]?.GetValue<string>() != invocationId
                        || evidence["Invocation"]?["UnitName"]?.GetValue<string>() != occurrence.Name
                        || evidence["Invocation"]?["Task"]?["Id"]?.GetValue<string>() != task.Id
                        || evidence["Invocation"]?["Task"]?["Script"]?.GetValue<string>() != task.Script
                        || !JsonNode.DeepEquals(evidence["Invocation"]?["Input"], prepared.Admission)
                    )
                        throw new InvalidDataException(
                            "Failed discussion receipt does not prove a settled read-only step."
                        );
                }
                continue;
            }
            if (occurrence.Attempts != 0)
                throw new InvalidOperationException(
                    "Reset of corrected agent invocations requires explicit reconciliation."
                );
            var sessionId = snapshot.Sessions[task.Session ?? node.Id];
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
            var binding = JsonNode.Parse(
                store.TryGetLatestArtifact(run.Id, "workflow-session:" + hash)?.Payload
                    ?? throw new InvalidOperationException("Hosted session binding is missing.")
            )!;
            var thread = binding["ThreadId"]!.GetValue<string>();
            if (
                binding["SessionId"]?.GetValue<string>() != sessionId
                || binding["WorkspaceId"]?.GetValue<string>() != prepared.Workspace.WorkspaceId
                || binding["WorkingDirectoryRelPath"]?.GetValue<string>() != prepared.Workspace.WorkingDirectoryRelPath
            )
                throw new InvalidDataException("Hosted session belongs to another workspace.");
            var invocation = new WorkflowInvocation
            {
                InstanceId = snapshot.InstanceId,
                InvocationId =
                    occurrence.ToolCallId ?? $"{snapshot.InstanceId}/{occurrence.Name}/{occurrence.Attempts}",
                UnitName = occurrence.Name,
                Task = task,
                Input = new JsonObject(),
            };
            var inputId = IdempotentInputId.Create(WorkflowAgentInvoker.InvocationKey(invocation), false, false);
            var status = await client.GetStatusByInputIdAsync(thread, inputId, ct).ConfigureAwait(false);
            if (status.Status is not ("Completed" or "Errored") || string.IsNullOrWhiteSpace(status.RunId))
                throw new InvalidOperationException("Accepted hosted input is not proven settled.");
            threads.Add(thread);
        }
        if (threads.Count == 0)
            throw new InvalidOperationException("No settled hosted review was found for this reset.");
        var barrier = new ReviewSubAgentCompletionBarrier(
            new S2SReviewSubAgentCompletionSource(client),
            TimeSpan.FromSeconds(options.ReviewSubAgentBarrierQuietSeconds),
            loggerFactory.CreateLogger<ReviewSubAgentCompletionBarrier>(),
            unknownQuiescence: TimeSpan.Zero
        );
        foreach (var thread in threads)
        {
            await client.RequireIdleAsync(thread, ct).ConfigureAwait(false);
            var tree =
                await barrier.TryConfirmSettledAsync(run, thread, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Hosted descendants are not settled.");
            foreach (var child in tree.Nodes)
                await client.RequireIdleAsync(child.ThreadId, ct).ConfigureAwait(false);
            await client.RequireIdleAsync(thread, ct).ConfigureAwait(false);
        }
    }
}
