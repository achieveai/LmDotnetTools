using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Scoped deterministic operations sharing the daemon's existing Git helpers and retention outbox.</summary>
internal sealed class WorkflowArtifactOperations(
    ReviewStore store,
    ReviewRun run,
    RepoIdentity repo,
    string repoRoot,
    string defaultBranch,
    ReviewBranchManager branchManager,
    SemaphoreSlim gitGate,
    ReviewArtifactBranchCapability artifactBranchCapability,
    WorkflowKnowledgeEdits? knowledgeEdits = null,
    Func<CancellationToken, Task>? verifyStoreOrigin = null
)
{
    internal const string RetentionOperation = "workflow_retain_artifacts";

    private string ArtifactBranch =>
        ReviewBranchManager.BuildReviewBranchName(repo, int.Parse(run.PrId, CultureInfo.InvariantCulture));

    /// <summary>Counts authoritative records; never infers findings, severity, or decisions from prose.</summary>
    public JsonObject CollectStatistics()
    {
        var artifacts = store.GetArtifacts(run.Id);
        var receipts = store.GetOutboxForRun(run.Id);
        return new JsonObject
        {
            ["RunId"] = run.Id.ToString(CultureInfo.InvariantCulture),
            ["ArtifactCount"] = artifacts.Count,
            ["ArtifactCountsByKind"] = JsonSerializer.SerializeToNode(
                artifacts
                    .GroupBy(a => a.ArtifactKind)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            ),
            ["ReceiptCount"] = receipts.Count,
            ["ReceiptCountsByStatus"] = JsonSerializer.SerializeToNode(
                receipts
                    .GroupBy(a => a.Status.ToString())
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            ),
        };
    }

    /// <summary>
    /// The run's artifact and receipt inventory with every row THIS retention creates removed, so the
    /// numbers do not depend on when the snapshot was taken (task #82, requirement 2).
    /// <para>
    /// <see cref="CollectStatistics"/> cannot be used for the exported <c>receipts.json</c> and the reason is
    /// not a rounding error. Retention enqueues its own outbox row and writes its own
    /// <c>review-artifact-branch</c> artifact as part of the very operation being counted, so a snapshot
    /// taken before the push and a snapshot taken after it disagree — and because the retained bundle is
    /// cached and replayed on resume, whichever one happened to be taken first is then frozen forever. A
    /// count that means "however far along we were when someone looked" is not an inventory.
    /// </para>
    /// <para>
    /// The fix is to make the counts INVARIANT rather than to time them better: the retention operation's
    /// own receipts and the three artifact kinds that describe the branch's lifecycle are excluded by name,
    /// which leaves a number that is identical before the push, after the push, and on every later replay.
    /// What is excluded is listed in the output, and the excluded facts are not lost — they are exactly the
    /// separately-verifiable branch receipt, which the redo path reads from the private store and proves
    /// against origin.
    /// </para>
    /// </summary>
    public JsonObject CollectRetentionInventory()
    {
        var artifacts = store
            .GetArtifacts(run.Id)
            .Where(artifact => !SelfReferentialArtifactKinds.Contains(artifact.ArtifactKind))
            .ToList();
        var receipts = store
            .GetOutboxForRun(run.Id)
            .Where(entry => !string.Equals(entry.Operation, RetentionOperation, StringComparison.Ordinal))
            .ToList();
        return new JsonObject
        {
            ["RunId"] = run.Id.ToString(CultureInfo.InvariantCulture),
            // Not a phase. The point is that there is no phase: these counts are the same whenever they
            // are taken, which is what makes a cached bundle's copy of them still true.
            ["SnapshotSemantics"] = "retention-independent",
            ["ExcludedArtifactKinds"] = new JsonArray([.. SelfReferentialArtifactKinds.Select(kind => (JsonNode)kind)]),
            ["ExcludedOperations"] = new JsonArray(RetentionOperation),
            ["ArtifactCount"] = artifacts.Count,
            ["ArtifactCountsByKind"] = JsonSerializer.SerializeToNode(
                artifacts
                    .GroupBy(a => a.ArtifactKind)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            ),
            ["ReceiptCount"] = receipts.Count,
            ["ReceiptCountsByStatus"] = JsonSerializer.SerializeToNode(
                receipts
                    .GroupBy(a => a.Status.ToString())
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
            ),
            // Where to check the one thing this file deliberately does not count.
            ["RetentionReceiptVerification"] =
                "This run's own retention receipt and branch receipt are excluded above because counting "
                + "them would make this file's numbers depend on when it was written. Verify them instead "
                + "against the private 'review-artifact-branch' artifact, whose recorded branch and pushed "
                + "SHA the redo command re-proves against origin before it will delete anything.",
        };
    }

    /// <summary>
    /// The artifact kinds retention and its redo write ABOUT the artifact branch. Counting them in the
    /// branch's own exported inventory is the self-reference that makes the count time-dependent.
    /// </summary>
    private static readonly HashSet<string> SelfReferentialArtifactKinds = new(StringComparer.Ordinal)
    {
        ReviewArtifactKinds.ArtifactBranchKind,
        ReviewArtifactKinds.ArtifactBranchRedoKind,
        ReviewArtifactKinds.ArtifactBranchQuarantineKind,
    };

    /// <summary>Builds knowledge files from explicitly bound validated extractions while holding only the Git operation gate.</summary>
    public async Task<IReadOnlyList<ReviewArtifactFile>> PrepareKnowledgeFilesAsync(
        JsonArray extractions,
        CancellationToken cancellationToken,
        JsonObject? safetyReview = null
    )
    {
        if (extractions.Count == 0)
            return [];
        WorkflowKnowledgeEdits.ValidateReview(extractions, safetyReview);
        if (run.PrLifecycleState != PrLifecycleState.Merged)
            throw new InvalidOperationException("Knowledge extraction retention requires a merged PR.");
        var helper =
            knowledgeEdits
            ?? throw new InvalidOperationException("Knowledge edit retention requires a configured scoped helper.");
        await gitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var repositoryLock = await HostRetentionWorkspace
                .AcquireRepositoryLockAsync(repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (verifyStoreOrigin is not null)
                await verifyStoreOrigin(cancellationToken).ConfigureAwait(false);
            await branchManager
                .CheckoutReviewBranchAsync(
                    repoRoot,
                    repo,
                    int.Parse(run.PrId, CultureInfo.InvariantCulture),
                    defaultBranch,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return await helper.PrepareAsync(extractions, cancellationToken, safetyReview).ConfigureAwait(false);
        }
        finally
        {
            gitGate.Release();
        }
    }

    /// <summary>Retains trusted, allowlisted files under this PR only, acknowledging the push before returning.</summary>
    /// <remarks>
    /// Task #82, requirement 1 — the capability check happens BEFORE the outbox is touched and before the
    /// git gate is taken, so an unauthorized daemon leaves neither a receipt nor a git trace. Requirement 5
    /// is the absence below: this method commits and pushes, and never deletes.
    /// </remarks>
    public async Task<JsonObject> RetainArtifactsAsync(
        IReadOnlyList<ReviewArtifactFile> files,
        CancellationToken cancellationToken,
        string? workflowInstanceId = null
    )
    {
        ValidateFiles(files);
        if (!artifactBranchCapability.AuthorizesPush(repo, run))
        {
            throw new InvalidOperationException(
                "Artifact-branch retention requires a command-scoped push capability naming this exact "
                    + "repository, pull request and head; this daemon has none."
            );
        }
        var hash = FilesHash(files);
        await gitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var repositoryLock = await HostRetentionWorkspace
                .AcquireRepositoryLockAsync(repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (verifyStoreOrigin is not null)
                await verifyStoreOrigin(cancellationToken).ConfigureAwait(false);
            var receipt = store.EnqueueOutbox(
                new OutboxEntry
                {
                    IdempotencyKey = RetentionKey(workflowInstanceId),
                    Provider = RepoIdentity.ToPublisherNamespace(repo.Provider),
                    ReviewRunId = run.Id,
                    Operation = RetentionOperation,
                    ArtifactKind = "workflow-artifacts",
                    Status = OutboxStatus.Pending,
                    BodyHash = hash,
                }
            );
            if (!string.Equals(receipt.BodyHash, hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Retention inputs changed after the operation was admitted.");
            }
            if (receipt.Status == OutboxStatus.Posted)
            {
                var replayed = RequireRetainedSha(receipt);
                RecordBranchReceipt(replayed, workflowInstanceId);
                return RetentionResult(replayed);
            }
            if (receipt.Status != OutboxStatus.Pending)
            {
                throw new InvalidOperationException("Retention has an unresolved operation receipt.");
            }
            await branchManager
                .CheckoutReviewBranchAsync(
                    repoRoot,
                    repo,
                    int.Parse(run.PrId, CultureInfo.InvariantCulture),
                    defaultBranch,
                    cancellationToken
                )
                .ConfigureAwait(false);
            foreach (var file in files)
                _ = WorkflowScriptInvoker.ResolveWorkspaceAsset(file.RelativePath, repoRoot);
            var result = await branchManager
                .CommitNotesAsync(
                    repoRoot,
                    new ReviewBotPublishRequest(
                        repo,
                        int.Parse(run.PrId, CultureInfo.InvariantCulture),
                        run.HeadSha,
                        defaultBranch,
                        files
                    ),
                    cancellationToken,
                    [.. files.Select(file => file.RelativePath)]
                )
                .ConfigureAwait(false);
            if (
                result.Outcome != ReviewBotPublishOutcome.Pushed
                || string.IsNullOrWhiteSpace(result.PushedSha)
                || !string.Equals(result.ReviewBranch, ArtifactBranch, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    "Required artifact retention failed; the artifact branch remains open."
                );
            }
            if (!store.TryTransitionOutbox(receipt.Id, OutboxStatus.Pending, OutboxStatus.Posted, result.PushedSha))
            {
                throw new InvalidOperationException("Could not durably acknowledge artifact retention.");
            }
            RecordBranchReceipt(result.PushedSha, workflowInstanceId);
            return RetentionResult(result.PushedSha);
        }
        finally
        {
            gitGate.Release();
        }
    }

    /// <summary>Closes only this merged PR's artifact branch, after its own durable retention acknowledgement.</summary>
    public async Task<JsonObject> CloseArtifactBranchAsync(
        CancellationToken cancellationToken,
        string? workflowInstanceId = null,
        IReadOnlyList<string>? retainedPaths = null
    )
    {
        if (run.PrLifecycleState != PrLifecycleState.Merged)
        {
            throw new InvalidOperationException("Artifact closure requires a merged PR.");
        }
        await gitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var repositoryLock = await HostRetentionWorkspace
                .AcquireRepositoryLockAsync(repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (verifyStoreOrigin is not null)
                await verifyStoreOrigin(cancellationToken).ConfigureAwait(false);
            var receipt =
                FindReceipt(workflowInstanceId)
                ?? throw new InvalidOperationException("Required artifacts have not been retained.");
            var sha = RequireRetainedSha(receipt);
            if (
                !await branchManager
                    .MergeToDefaultAsync(repoRoot, ArtifactBranch, defaultBranch, cancellationToken)
                    .ConfigureAwait(false)
                || !await branchManager
                    .VerifyClosureAsync(
                        repoRoot,
                        ArtifactBranch,
                        defaultBranch,
                        sha,
                        retainedPaths ?? [$"PRs/{ArtifactBranch["review/".Length..]}"],
                        cancellationToken
                    )
                    .ConfigureAwait(false)
            )
            {
                throw new InvalidOperationException("Artifact branch closure failed; retry inputs remain retained.");
            }
            return new JsonObject { ["ArtifactBranch"] = ArtifactBranch, ["Closed"] = true };
        }
        finally
        {
            gitGate.Release();
        }
    }

    /// <summary>Returns only a durably acknowledged result, without attempting a write replay.</summary>
    public JsonObject? ReconcileRetention(
        string? workflowInstanceId = null,
        IReadOnlyList<ReviewArtifactFile>? files = null
    )
    {
        var receipt = FindReceipt(workflowInstanceId);
        return
            receipt is { Status: OutboxStatus.Posted }
            && !string.IsNullOrWhiteSpace(receipt.ProviderResponseId)
            && (files is null || receipt.BodyHash == FilesHash(files))
            ? RetentionResult(receipt.ProviderResponseId)
            : null;
    }

    /// <summary>Checks remote closure and retained content without merging, committing, or pushing.</summary>
    public async Task<JsonObject?> ReconcileClosureAsync(
        CancellationToken cancellationToken,
        string? workflowInstanceId = null,
        IReadOnlyList<string>? retainedPaths = null
    )
    {
        var retained = ReconcileRetention(workflowInstanceId);
        if (run.PrLifecycleState != PrLifecycleState.Merged || retained is null)
            return null;
        await gitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var repositoryLock = await HostRetentionWorkspace
                .AcquireRepositoryLockAsync(repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (verifyStoreOrigin is not null)
                await verifyStoreOrigin(cancellationToken).ConfigureAwait(false);
            return await branchManager
                .VerifyClosureAsync(
                    repoRoot,
                    ArtifactBranch,
                    defaultBranch,
                    retained["RetainedSha"]!.GetValue<string>(),
                    retainedPaths ?? [$"PRs/{ArtifactBranch["review/".Length..]}"],
                    cancellationToken
                )
                .ConfigureAwait(false)
                ? new JsonObject { ["ArtifactBranch"] = ArtifactBranch, ["Closed"] = true }
                : null;
        }
        finally
        {
            gitGate.Release();
        }
    }

    private static string FilesHash(IReadOnlyList<ReviewArtifactFile> files) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
                )
            )
        );

    private OutboxEntry? FindReceipt(string? workflowInstanceId) =>
        store
            .GetOutboxForRun(run.Id)
            .SingleOrDefault(entry => entry.IdempotencyKey == RetentionKey(workflowInstanceId));

    private string RetentionKey(string? workflowInstanceId) => BuildRetentionKey(repo, run, workflowInstanceId);

    /// <summary>
    /// The idempotency key of the retention receipt one workflow instance owns. Exposed because a run can
    /// accumulate SEVERAL retention rows — the key includes the workflow instance id, so a re-run under a new
    /// instance enqueues its own row rather than replaying the first — and the redo path must therefore
    /// identify the receipt belonging to the branch receipt it recorded instead of assuming there is only one.
    /// </summary>
    /// <param name="repo">Identity of the reviewed repository.</param>
    /// <param name="run">The review run the receipt belongs to.</param>
    /// <param name="workflowInstanceId">The workflow instance, or <c>null</c> for the run-scoped default.</param>
    internal static string BuildRetentionKey(RepoIdentity repo, ReviewRun run, string? workflowInstanceId) =>
        IdempotencyKey.Build(
            new IdempotencyKeyComponents(
                RepoIdentity.ToPublisherNamespace(repo.Provider),
                repo.OrgOrOwner,
                repo.Project,
                repo.RepoStableId ?? repo.NormalizedKey,
                run.PrId,
                RetentionOperation,
                "workflow-artifacts",
                workflowInstanceId ?? run.Id.ToString(CultureInfo.InvariantCulture),
                run.HeadSha,
                run.VariantId
            )
        );

    private JsonObject RetentionResult(string sha) =>
        new() { ["ArtifactBranch"] = ArtifactBranch, ["RetainedSha"] = sha };

    /// <summary>
    /// Persists the branch name and the SHA that was actually pushed (task #82, requirement 3). The outbox
    /// receipt already carries the SHA, but only as an opaque <c>ProviderResponseId</c>; the redo path needs
    /// the branch, repo key and head alongside it to prove ownership before it deletes anything, and
    /// re-deriving the branch at redo time would prove nothing about what was pushed.
    /// <para>
    /// Written on the replay path too, so a retention that short-circuits on an existing receipt still
    /// leaves the record the redo path reads. Identical payloads are not duplicated.
    /// </para>
    /// </summary>
    private void RecordBranchReceipt(string pushedSha, string? workflowInstanceId)
    {
        var payload = JsonSerializer.Serialize(
            new ReviewArtifactBranchReceipt(
                ArtifactBranch,
                pushedSha,
                repo.NormalizedKey,
                run.PrId,
                run.HeadSha,
                workflowInstanceId ?? run.Id.ToString(CultureInfo.InvariantCulture)
            )
        );
        if (store.TryGetLatestArtifact(run.Id, ReviewArtifactKinds.ArtifactBranchKind)?.Payload == payload)
        {
            return;
        }
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = ReviewArtifactKinds.ArtifactBranchKind,
                ArtifactSchemaVersion = ReviewArtifactKinds.ArtifactBranchSchemaVersion,
                Provider = RepoIdentity.ToPublisherNamespace(repo.Provider),
                Payload = payload,
            }
        );
    }

    private static string RequireRetainedSha(OutboxEntry receipt) =>
        receipt.Status == OutboxStatus.Posted && !string.IsNullOrWhiteSpace(receipt.ProviderResponseId)
            ? receipt.ProviderResponseId
            : throw new InvalidOperationException("Required artifact retention has no successful durable receipt.");

    private void ValidateFiles(IReadOnlyList<ReviewArtifactFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("Required retention cannot contain zero artifacts.", nameof(files));
        }
        var prefix = $"PRs/{ArtifactBranch["review/".Length..]}/";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = file.RelativePath;
            if (path.StartsWith("KnowledgeBase/", StringComparison.Ordinal))
            {
                if (run.PrLifecycleState != PrLifecycleState.Merged || knowledgeEdits is null)
                    throw new ArgumentException(
                        "Knowledge retention requires a merged PR and a configured scoped helper.",
                        nameof(files)
                    );
                WorkflowKnowledgeEdits.ValidatePath(path, repo, allowListings: true);
            }
            else if (!path.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException($"Artifact path must be contained in '{prefix}'.", nameof(files));
            if (
                path.EndsWith('/')
                || path.Contains('\\')
                || path.Contains(':')
                || path.Contains('\0')
                || path.Split('/').Any(segment => segment is "." or ".." or "")
                || !paths.Add(path)
            )
            {
                throw new ArgumentException(
                    $"Artifact path must be unique and contained in '{prefix}'.",
                    nameof(files)
                );
            }
            if (path.StartsWith("PRs/", StringComparison.Ordinal))
                WorkflowOperationDispatcher.ValidatePublicArtifact(
                    file,
                    ReviewArtifactExportBinding.Build(run, repo, ArtifactBranch)
                );
        }
    }
}
