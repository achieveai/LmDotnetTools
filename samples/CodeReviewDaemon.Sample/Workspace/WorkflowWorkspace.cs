using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>The trusted preparation result, separate from the authored workflow's JSON contracts.</summary>
internal sealed record WorkflowWorkspacePreparation(
    ReviewSlot Slot,
    PreparedCheckout Checkout,
    PreparedReviewWorkspace Workspace,
    JsonObject Output,
    string AssignmentId,
    JsonObject Admission
);

/// <summary>A parent-owned resource assignment stored in the existing artifact table for scoped CLI calls.</summary>
internal sealed record WorkflowWorkspaceAssignment(
    ReviewSlot Slot,
    bool Active,
    string AssignmentId,
    string WorkflowInstanceId
);

internal sealed class WorkspaceCapacityUnavailableException()
    : InvalidOperationException("No review workspace is currently available.");

/// <summary>Separates parent-process slot lifetime from deterministic preparation performed by a scoped script process.</summary>
internal sealed class WorkflowWorkspace
{
    internal const string AssignmentArtifactKind = "workflow-workspace-assignment";
    internal const string PreparationArtifactKind = "workflow-workspace-prepared";
    internal const string CheckoutArtifactKind = "workflow-workspace-checkout";
    private readonly ReviewStore _store;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ReviewSlotWorkspace _slots;
    private readonly Func<ReviewSlot, ReviewRun, CancellationToken, Task<PreparedReviewWorkspace>> _adoptWorkspace;
    private readonly Func<ReviewRun, JsonObject, CancellationToken, Task<JsonObject>>? _contextReader;
    private readonly ReviewWorkspaceOperations _operations;
    private readonly ConcurrentDictionary<long, WorkflowWorkspaceAssignment> _leases = new();

    public WorkflowWorkspace(
        ReviewStore store,
        CodeReviewDaemonOptions options,
        ReviewSlotWorkspace slots,
        Func<ReviewSlot, ReviewRun, CancellationToken, Task<PreparedReviewWorkspace>> adoptWorkspace,
        ILoggerFactory loggerFactory,
        Func<ReviewRun, JsonObject, CancellationToken, Task<JsonObject>>? contextReader = null
    )
    {
        _store = store;
        _options = options;
        _slots = slots;
        _adoptWorkspace = adoptWorkspace;
        _contextReader = contextReader;
        _operations = new ReviewWorkspaceOperations(options, loggerFactory.CreateLogger<WorkflowWorkspace>());
    }

    /// <summary>Called only by the parent daemon; scripts consume the durable assignment without leasing again.</summary>
    public async Task<ReviewSlot> AcquireAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    ) =>
        await AcquireCoreAsync(run, workflowInstanceId, wait: true, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("A blocking workspace acquisition returned no slot.");

    /// <summary>Attempts a fresh or affinity workspace lease without blocking the poll loop.</summary>
    public Task<ReviewSlot?> TryAcquireAsync(
        ReviewRun run,
        string workflowInstanceId,
        CancellationToken cancellationToken
    ) => AcquireCoreAsync(run, workflowInstanceId, wait: false, cancellationToken);

    private async Task<ReviewSlot?> AcquireCoreAsync(
        ReviewRun run,
        string workflowInstanceId,
        bool wait,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        if (_leases.TryGetValue(run.Id, out var existing))
        {
            if (string.Equals(existing.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
            {
                return existing.Slot;
            }
            if (!wait)
            {
                return null;
            }
            throw new WorkspaceCapacityUnavailableException();
        }
        WorkflowWorkspaceAssignment? previous = null;
        if (_store.TryGetLatestArtifact(run.Id, AssignmentArtifactKind) is { } prior)
        {
            previous =
                JsonSerializer.Deserialize<WorkflowWorkspaceAssignment>(prior.Payload)
                ?? throw new InvalidDataException("Workflow workspace assignment is invalid.");
            if (previous.Active)
            {
                if (!wait)
                {
                    return null;
                }
                throw new WorkspaceCapacityUnavailableException();
            }
        }
        var slot = previous switch
        {
            null when wait => await _slots.Pool.LeaseAsync(cancellationToken).ConfigureAwait(false),
            null => await _slots.Pool.TryLeaseAsync(cancellationToken).ConfigureAwait(false),
            not null when wait => await _slots
                .Pool.LeasePreferredAsync(previous.Slot, cancellationToken)
                .ConfigureAwait(false),
            _ => await _slots.Pool.TryLeasePreferredAsync(previous.Slot, cancellationToken).ConfigureAwait(false),
        };
        if (slot is null)
        {
            return null;
        }
        var assignment = new WorkflowWorkspaceAssignment(
            slot,
            Active: true,
            Guid.NewGuid().ToString("N"),
            workflowInstanceId
        );
        if (!_leases.TryAdd(run.Id, assignment))
        {
            await _slots.Pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            var winner = _leases[run.Id];
            if (IsOwner(winner, workflowInstanceId))
            {
                return winner.Slot;
            }
            if (!wait)
            {
                return null;
            }
            throw new WorkspaceCapacityUnavailableException();
        }
        try
        {
            Save(run, AssignmentArtifactKind, assignment);
            return slot;
        }
        catch
        {
            _leases.TryRemove(run.Id, out _);
            await _slots.Pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public ReviewSlot ReadAssignedSlot(ReviewRun run)
    {
        var assignment = ReadAssignment(run);
        return assignment.Slot;
    }

    /// <summary>Restores the original resource lease after restart; never prepares or cleans an unfinished workspace.</summary>
    public async Task<ReviewSlot> RecoverAsync(
        ReviewRun run,
        string assignmentId,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        var assignment = ReadAssignment(run);
        if (assignment.AssignmentId != assignmentId)
        {
            throw new InvalidOperationException("Recovery does not match the persisted workspace assignment.");
        }
        if (string.IsNullOrWhiteSpace(assignment.WorkflowInstanceId))
        {
            throw new InvalidDataException("Workflow workspace assignment has no owner.");
        }
        if (!string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal))
        {
            throw new WorkspaceCapacityUnavailableException();
        }
        if (_leases.TryGetValue(run.Id, out var existing))
        {
            return
                existing.AssignmentId == assignmentId
                && string.Equals(existing.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal)
                ? existing.Slot
                : throw new InvalidOperationException("Run already holds a different workspace assignment.");
        }
        var slot = await _slots.Pool.RecoverLeaseAsync(assignment.Slot, cancellationToken).ConfigureAwait(false);
        if (!_leases.TryAdd(run.Id, assignment))
        {
            await _slots.Pool.RetireAsync(slot, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Run was concurrently recovered; the extra address has been retired.");
        }
        return slot;
    }

    public WorkflowWorkspaceAssignment ReadAssignment(ReviewRun run)
    {
        var assignment = Read<WorkflowWorkspaceAssignment>(run.Id, AssignmentArtifactKind);
        if (!assignment.Active || string.IsNullOrWhiteSpace(assignment.AssignmentId))
        {
            throw new InvalidOperationException("This run's workspace assignment is no longer active.");
        }
        return assignment;
    }

    public WorkflowWorkspacePreparation ReadPreparation(ReviewRun run)
    {
        var prepared = Read<WorkflowWorkspacePreparation>(run.Id, PreparationArtifactKind);
        var assignment = ReadAssignment(run);
        if (prepared.Slot != assignment.Slot || prepared.AssignmentId != assignment.AssignmentId)
        {
            throw new InvalidOperationException("Prepared workspace belongs to an obsolete slot assignment.");
        }
        return prepared;
    }

    /// <summary>Proves preparation belongs to the active frozen admission and its context artifact still exists.</summary>
    public async Task<JsonObject?> ReconcilePreparationAsync(
        ReviewRun run,
        JsonObject admission,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        WorkflowWorkspacePreparation prepared;
        try
        {
            prepared = ReadPreparation(run);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (!IsOwner(ReadAssignment(run), workflowInstanceId))
            return null;
        if (!JsonNode.DeepEquals(prepared.Admission, admission))
            return null;
        var name = $"workflow-context-{run.Id.ToString(CultureInfo.InvariantCulture)}-{prepared.AssignmentId}.json";
        var context = await _slots
            .HostFileSystem.ReadFileAsync(
                $"{prepared.Checkout.NotesDir.TrimEnd('/', '\\')}/{name}",
                checked(_options.Limits.MaxArtifactPayloadChars * 4L),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (context.Content is null)
            return null;
        try
        {
            return JsonNode.Parse(context.Content) is JsonObject ? (JsonObject)prepared.Output.DeepClone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs real preparation against the parent's assigned address and writes the provided context unchanged.</summary>
    public async Task<WorkflowWorkspacePreparation> PrepareAssignedAsync(
        ReviewRun run,
        JsonObject admission,
        string workflowInstanceId,
        CancellationToken cancellationToken
    )
    {
        if (_contextReader is null)
        {
            throw new InvalidOperationException(
                "A trusted frozen context reader is required for workflow preparation."
            );
        }
        if (
            admission["PrId"]?.GetValue<string>() != run.PrId
            || admission["HeadSha"]?.GetValue<string>() != run.HeadSha
        )
        {
            throw new InvalidOperationException("Prepared input does not match the trusted run identity.");
        }
        var assignment = ReadAssignment(run);
        if (!IsOwner(assignment, workflowInstanceId))
        {
            throw new WorkspaceCapacityUnavailableException();
        }
        var slot = assignment.Slot;
        var repo = _store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        var provider = RepoIdentity.ToPublisherNamespace(repo.Provider);
        var storeUrl =
            _options.ResolvedStoreUrl
            ?? throw new InvalidOperationException("Workflow preparation requires a configured review store.");
        await _slots.HostPreparer.EnsureStoreAsync(slot.StorePath, storeUrl, cancellationToken).ConfigureAwait(false);
        var submodule =
            await _operations
                .ResolveStoreSubmodulePathAsync(_slots.HostFileSystem, slot.StorePath, repo, provider)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Reviewed repository is not a configured submodule of the assigned review store."
            );
        var branch = ReviewBranchManager.BuildReviewBranchName(repo, int.Parse(run.PrId, CultureInfo.InvariantCulture));
        var notes = $"PRs/{branch["review/".Length..]}";
        var policy = DaemonOperationPolicy.BuildForRun(
            repo,
            _options.ReviewBotRepoUrl,
            allowWriteOperations: false,
            allowedSubmodules: _operations.BuildStoreSubmoduleAllowList(run, repo)
        );
        var checkout = await _operations
            .PrepareWithRecoveryAsync(
                _slots.HostPreparer,
                run,
                slot.StorePath,
                slot.ScratchPath,
                storeUrl,
                submodule,
                branch,
                notes,
                policy,
                cancellationToken
            )
            .ConfigureAwait(false);
        // The reader can resolve exact checkout evidence through this artifact without needing a task-specific C# input type.
        Save(run, CheckoutArtifactKind, checkout);
        var context = await _contextReader(run, admission, cancellationToken).ConfigureAwait(false);
        var name = $"workflow-context-{run.Id.ToString(CultureInfo.InvariantCulture)}-{assignment.AssignmentId}.json";
        var hostContextPath = $"{checkout.NotesDir.TrimEnd('/', '\\')}/{name}";
        await _slots
            .HostFileSystem.WriteFileAsync(hostContextPath, context.ToJsonString(), cancellationToken)
            .ConfigureAwait(false);
        var workspace = await _adoptWorkspace(slot, run, cancellationToken).ConfigureAwait(false);
        var result = new WorkflowWorkspacePreparation(
            slot,
            checkout,
            workspace,
            new JsonObject
            {
                ["PrId"] = run.PrId,
                ["HeadSha"] = run.HeadSha,
                ["WindowId"] = admission["WindowId"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                ["ContextArtifact"] = $"/workspace/store/{notes}/{name}",
            },
            assignment.AssignmentId,
            (JsonObject)admission.DeepClone()
        );
        if (ReadAssignment(run).AssignmentId != assignment.AssignmentId)
        {
            throw new InvalidOperationException("Workspace assignment changed during preparation.");
        }
        Save(run, PreparationArtifactKind, result);
        return result;
    }

    /// <summary>Invalidates the CLI assignment before returning a settled slot; unresolved activity retires its address.</summary>
    public async Task ReleaseOrRetireAsync(
        long runId,
        string workflowInstanceId,
        bool workSettled,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowInstanceId);
        if (!_leases.TryGetValue(runId, out var current))
        {
            return;
        }
        if (!IsOwner(current, workflowInstanceId))
        {
            throw new InvalidOperationException(
                "Refusing to release another workflow instance's workspace assignment."
            );
        }
        if (!_leases.TryRemove(runId, out var assignment))
            return;
        try
        {
            var run = _store.GetReviewRun(runId) ?? throw new InvalidOperationException("Assigned run disappeared.");
            if (ReadAssignment(run).AssignmentId != assignment.AssignmentId)
            {
                throw new InvalidOperationException("Refusing to release a different workspace assignment.");
            }
            Save(run, AssignmentArtifactKind, assignment with { Active = false });
        }
        catch
        {
            // Failed invalidation cannot authorize reuse by another run while a CLI still resolves this assignment.
            await _slots.Pool.RetireAsync(assignment.Slot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await (
            workSettled
                ? _slots.Pool.ReturnAsync(assignment.Slot, CancellationToken.None)
                : _slots.Pool.RetireAsync(assignment.Slot, CancellationToken.None)
        ).ConfigureAwait(false);
    }

    private static bool IsOwner(WorkflowWorkspaceAssignment assignment, string workflowInstanceId) =>
        string.Equals(assignment.WorkflowInstanceId, workflowInstanceId, StringComparison.Ordinal);

    /// <summary>Called by parent reconciliation only after the old address was quarantined or its owner proved stopped.</summary>
    public void InvalidateReconciledAssignment(ReviewRun run, string assignmentId)
    {
        var assignment = ReadAssignment(run);
        if (assignment.AssignmentId != assignmentId || _leases.ContainsKey(run.Id))
        {
            throw new InvalidOperationException("Reconciliation does not match an abandoned assignment.");
        }
        Save(run, AssignmentArtifactKind, assignment with { Active = false });
    }

    private T Read<T>(long runId, string kind) =>
        JsonSerializer.Deserialize<T>(
            _store.TryGetLatestArtifact(runId, kind)?.Payload
                ?? throw new InvalidOperationException($"Missing required '{kind}' artifact.")
        ) ?? throw new InvalidOperationException($"Invalid '{kind}' artifact.");

    private void Save<T>(ReviewRun run, string kind, T value) =>
        _store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = 1,
                ArtifactKind = kind,
                Provider = RepoIdentity.ToPublisherNamespace(_store.GetRepo(run.RepoId)!.Provider),
                Payload = JsonSerializer.Serialize(value),
            }
        );
}
