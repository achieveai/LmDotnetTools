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

/// <summary>Combines immutable admission data with exact checkout evidence. No semantic selection occurs here.</summary>
internal sealed class WorkflowContextReader(
    ReviewStore store,
    string runDirectoryRoot,
    int maximumCharacters,
    Func<ReviewRun, CancellationToken, Task<JsonNode?>>? readLinkedContext = null
)
{
    public async Task<JsonObject> ReadAsync(
        ReviewRun run,
        JsonObject admission,
        ReviewRunSession session,
        CancellationToken ct
    )
    {
        var roundKind =
            admission["Route"]?.GetValue<string>() == "merged"
                ? WorkflowRoundKind.Merged
                : WorkflowRoundKind.Discussion;
        var instanceId =
            admission["Route"]?.GetValue<string>() == "new_head"
                ? $"review-run-{run.Id}"
                : "review-round-"
                    + store
                        .GetPendingWorkflowRounds(run.RepoId)
                        .Single(r =>
                            r.PrId == run.PrId
                            && r.HeadSha == run.HeadSha
                            && r.EventKey == admission["WindowId"]?.GetValue<string>()
                            && r.Kind == roundKind
                        )
                        .Id;
        var directory = Path.Combine(
            runDirectoryRoot,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)))
        );
        var scopePath = WorkflowScriptInvoker.ResolveWorkspaceAsset("scope.json", directory);
        using var scopeReader = File.OpenText(scopePath);
        var buffer = new char[maximumCharacters + 1];
        var count = await scopeReader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (count > maximumCharacters)
            throw new InvalidDataException("Frozen workflow context exceeds its limit.");
        var scopeText = new string(buffer, 0, count);
        var scope =
            JsonNode.Parse(scopeText) as JsonObject ?? throw new InvalidDataException("Workflow scope is invalid.");
        if (
            scope["ReviewRunId"]?.GetValue<long>() != run.Id
            || scope["WorkflowInstanceId"]?.GetValue<string>() != instanceId
            || !JsonNode.DeepEquals(scope["Admission"], admission)
            || scope["FrozenContext"] is not JsonObject frozen
        )
            throw new InvalidDataException("Workflow context does not match its frozen admission.");
        var checkout =
            JsonSerializer.Deserialize<PreparedCheckout>(
                store.TryGetLatestArtifact(run.Id, WorkflowWorkspace.CheckoutArtifactKind)?.Payload
                    ?? throw new InvalidDataException("Prepared checkout evidence is missing.")
            ) ?? throw new InvalidDataException("Prepared checkout evidence is invalid.");
        var git = new GitRunner(session.CommandRunner);
        var head = await git.RunAsync(["rev-parse", "HEAD"], checkout.TargetDir, ct).ConfigureAwait(false);
        if (
            !head.Succeeded
            || !IsSha(checkout.CheckoutSha ?? "")
            || !string.Equals(head.Stdout.Trim(), checkout.CheckoutSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(checkout.SourceHeadSha, run.HeadSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(checkout.TargetBaseSha, run.BaseSha, StringComparison.OrdinalIgnoreCase)
            || (
                run.MergeSha is not null
                && !string.Equals(checkout.CheckoutSha, run.MergeSha, StringComparison.OrdinalIgnoreCase)
            )
        )
            throw new InvalidDataException("Prepared checkout does not match admitted merge provenance.");
        var parents = await git.RunAsync(["show", "-s", "--format=%P", "HEAD"], checkout.TargetDir, ct)
            .ConfigureAwait(false);
        if (
            !parents.Succeeded
            || !parents
                .Stdout.Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual([run.BaseSha, run.HeadSha], StringComparer.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("Prepared merge parents do not match admitted target/source commits.");
        var actualBase = await git.RunAsync(["merge-base", run.BaseSha, run.HeadSha], checkout.TargetDir, ct)
            .ConfigureAwait(false);
        if (
            !actualBase.Succeeded
            || !string.Equals(actualBase.Stdout.Trim(), checkout.MergeBaseSha, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidDataException("Prepared merge base is not the exact admitted merge base.");
        var baseSha =
            checkout.MergeBaseSha
            ?? throw new InvalidDataException("An exact merge base is required for workflow review.");
        if (!IsSha(baseSha) || !IsSha(run.HeadSha))
            throw new InvalidDataException("Invalid checkout commit identity.");
        var diff = await git.RunAsync(
                ["diff", "--no-ext-diff", "--no-textconv", "--unified=3", baseSha, run.HeadSha, "--"],
                checkout.TargetDir,
                ct
            )
            .ConfigureAwait(false);
        if (!diff.Succeeded || diff.Stdout.Length > maximumCharacters)
            throw new InvalidDataException("Complete bounded diff evidence is unavailable.");
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = "workflow-diff",
                ArtifactSchemaVersion = 1,
                Provider = RepoIdentity.ToPublisherNamespace(store.GetRepo(run.RepoId)!.Provider),
                Payload = new JsonObject
                {
                    ["BaseSha"] = baseSha,
                    ["HeadSha"] = run.HeadSha,
                    ["Diff"] = diff.Stdout,
                }.ToJsonString(),
            }
        );
        var result = new JsonObject
        {
            ["Admission"] = admission.DeepClone(),
            ["UntrustedData"] = frozen.DeepClone(),
            ["Evidence"] = new JsonObject
            {
                ["BaseSha"] = baseSha,
                ["HeadSha"] = run.HeadSha,
                ["Diff"] = diff.Stdout,
                ["TargetDirectory"] = MountedPath(checkout.TargetDir, checkout.StoreRoot),
                ["HistoryDirectory"] = MountedPath(checkout.NotesDir, checkout.StoreRoot),
                ["RepositorySlug"] = ReviewBranchManager.RepoSlug(store.GetRepo(run.RepoId)!),
            },
        };
        if (frozen["ContextManifestVersion"]?.GetValue<int>() == 1)
        {
            result["ContextManifestVersion"] = 1;
            var repo = store.GetRepo(run.RepoId)!;
            result["Evidence"]!["Repository"] = new JsonObject
            {
                ["Provider"] = RepoIdentity.ToPublisherNamespace(repo.Provider),
                ["Organization"] = repo.OrgOrOwner,
                ["Project"] = repo.Project,
                ["Name"] = repo.RepoName,
            };
            result["Evidence"]!["TargetBaseSha"] = run.BaseSha;
            result["Evidence"]!["CheckoutSha"] = checkout.CheckoutSha;
            result["Evidence"]!["KnowledgeRoots"] = new JsonArray(
                MountedPath(Path.Combine(checkout.StoreRoot, "KnowledgeBase", "system"), checkout.StoreRoot),
                MountedPath(
                    Path.Combine(
                        checkout.StoreRoot,
                        "KnowledgeBase",
                        ReviewBranchManager.RepoSlug(store.GetRepo(run.RepoId)!)
                    ),
                    checkout.StoreRoot
                )
            );
        }
        else
        {
            // Frozen older workflows still require their exact-entry knowledge contract.
            result["Evidence"]!["KnowledgeEntryPaths"] = await WorkflowKnowledgeEdits
                .ReadEntryPathsAsync(checkout.StoreRoot, store.GetRepo(run.RepoId)!, session.FileSystem, ct)
                .ConfigureAwait(false);
        }
        if (readLinkedContext is not null)
            result["UntrustedData"]!["LinkedWorkContext"] = (
                await readLinkedContext(run, ct).ConfigureAwait(false)
            )?.DeepClone();
        if (result.ToJsonString().Length > maximumCharacters)
            throw new InvalidDataException("Workflow context exceeds its limit.");
        return result;
    }

    internal static string MountedPath(string path, string storeRoot)
    {
        var relative = Path.GetRelativePath(storeRoot, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException("Checkout path is outside its assigned store.");
        var root = storeRoot.TrimEnd('/', '\\');
        return relative == "." ? root : root + "/" + relative;
    }

    private static bool IsSha(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
