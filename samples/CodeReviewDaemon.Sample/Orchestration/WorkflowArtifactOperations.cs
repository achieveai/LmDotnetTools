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

    /// <summary>Builds knowledge files from explicitly bound validated extractions while holding only the Git operation gate.</summary>
    public async Task<IReadOnlyList<ReviewArtifactFile>> PrepareKnowledgeFilesAsync(
        JsonArray extractions,
        CancellationToken cancellationToken
    )
    {
        if (extractions.Count == 0)
            return [];
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
            return await helper.PrepareAsync(extractions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gitGate.Release();
        }
    }

    /// <summary>Retains trusted, allowlisted files under this PR only, acknowledging the push before returning.</summary>
    public async Task<JsonObject> RetainArtifactsAsync(
        IReadOnlyList<ReviewArtifactFile> files,
        CancellationToken cancellationToken,
        string? workflowInstanceId = null
    )
    {
        ValidateFiles(files);
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
                return RetentionResult(RequireRetainedSha(receipt));
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

    private string RetentionKey(string? workflowInstanceId) =>
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
        }
    }
}
