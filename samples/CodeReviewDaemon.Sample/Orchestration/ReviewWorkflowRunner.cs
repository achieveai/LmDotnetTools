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

    public ReviewWorkflowRunner(
        string workflowPath,
        string runDirectoryRoot,
        ReviewStore reviewStore,
        IWorkflowStore workflowStore,
        WorkflowWorkspace workspace,
        Func<ReviewRun, string, string, CancellationToken, Task<IWorkflowTaskInvoker>> invokerFactory,
        Func<ReviewRun, WorkflowRound?, IReadOnlyDictionary<string, string>>? hostSessionBindings = null,
        TimeSpan? invocationTimeout = null
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
            runtimeStatus = await runtime
                .RunAutomaticAsync(_workflowStore, instanceId, invoker, cancellationToken)
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
                releaseLease = true;
                CompleteAdmission(storedRun, storedRound, instanceId);
            }
            else
            {
                releaseLease = IsSettled(durable);
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
            if (hasLease && runtimeStatus != WorkflowInvocationStatus.Unknown)
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
    public async Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken)
    {
        foreach (var run in _reviewStore.ListActiveWorkflowWorkspaceRuns())
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
                _ = await _workspace
                    .RecoverAsync(run, assignment.AssignmentId, owner, cancellationToken)
                    .ConfigureAwait(false);
                if (await IsOwnerDurablySettledAsync(run, owner, cancellationToken).ConfigureAwait(false))
                {
                    await _workspace
                        .ReleaseOrRetireAsync(run.Id, owner, workSettled: true, CancellationToken.None)
                        .ConfigureAwait(false);
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
            stored.RepoId != run.RepoId
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
                WorkflowRoundKind.Merged => "merged",
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
        if (prior is null)
        {
            return await _workspace.TryAcquireAsync(run, workflowInstanceId, cancellationToken).ConfigureAwait(false)
                is not null;
        }
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
