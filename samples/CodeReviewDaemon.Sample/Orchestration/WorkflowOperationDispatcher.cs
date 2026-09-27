using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Dispatches trusted foreground CLI operations after resolving the persisted run scope.</summary>
internal sealed class WorkflowOperationDispatcher(
    ReviewStore store,
    WorkflowWorkspace workspace,
    CodeReviewDaemonOptions options,
    string trustedRunDirectoryRoot,
    Func<ReviewArtifactBranchCapability> artifactBranchCapability,
    Func<ReviewRun, CancellationToken, Task<WorkflowArtifactOperations>> artifactOperations,
    Func<ReviewRun, CancellationToken, Task<JsonObject>>? readDiscussion = null
)
{
    private sealed record Scope(
        ReviewRun Run,
        string InstanceId,
        string Directory,
        JsonObject Admission,
        JsonObject FrozenContext,
        JsonArray Extractions,
        JsonObject? KnowledgeReview,
        JsonObject? Canonical
    );

    private sealed record RetentionBundle(
        int ExportSchema,
        string WorkflowInstanceId,
        long ReviewRunId,
        string ExtractionHash,
        bool ExportsFullReviewOutput,
        IReadOnlyList<ReviewArtifactFile> Files
    );

    internal static bool Supports(string operation) =>
        operation
            is "prepare-review"
                or "prepare-independent"
                or "fetch-discussion"
                or "collect-publication"
                or "prepare-discussion"
                or "prepare-history"
                or "collect-statistics"
                or "retain-artifacts"
                or "close-artifact-branch";

    public async Task<JsonNode> DispatchAsync(
        string operation,
        JsonObject context,
        JsonNode input,
        CancellationToken cancellationToken
    )
    {
        var scope = await ResolveScopeAsync(operation, context, input, cancellationToken).ConfigureAwait(false);
        if (operation.StartsWith("prepare-", StringComparison.Ordinal))
        {
            var prepared = await workspace
                .PrepareAssignedAsync(scope.Run, scope.Admission, scope.InstanceId, cancellationToken)
                .ConfigureAwait(false);
            return PreparationOutput(operation, scope.Run, prepared.Output);
        }
        if (operation == "collect-publication")
            return CollectedPublication(scope.Run);
        if (operation == "fetch-discussion")
            return await CaptureDiscussionAsync(scope, cancellationToken).ConfigureAwait(false);
        JsonObject? frozenContextDigest = null;
        if (operation == "retain-artifacts")
            frozenContextDigest = PersistCanonical(scope);
        var operations = await artifactOperations(scope.Run, cancellationToken).ConfigureAwait(false);
        return operation switch
        {
            "collect-statistics" => operations.CollectStatistics(),
            "retain-artifacts" => await operations
                .RetainArtifactsAsync(
                    await ReadOrCaptureBundleAsync(scope, operations, frozenContextDigest, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken,
                    scope.InstanceId
                )
                .ConfigureAwait(false),
            "close-artifact-branch" => await CloseAsync(operations, scope, input, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported workflow operation."),
        };
    }

    private JsonObject PreparationOutput(string operation, ReviewRun run, JsonObject prepared)
    {
        var result = (JsonObject)prepared.DeepClone();
        if (operation == "prepare-independent")
            result["PublicationMode"] = run.Mode == "collect-only" ? "collect_only" : "post";
        return result;
    }

    private static JsonObject CollectedPublication(ReviewRun run)
    {
        if (run.Mode != "collect-only")
            throw new InvalidOperationException("Markdown collection requires a collect-only run.");
        return new JsonObject
        {
            ["Outcome"] = "no_op",
            ["Description"] = "Proposed comments retained as Markdown; no source PR publication.",
            ["Actions"] = new JsonArray(),
        };
    }

    private async Task<JsonObject> CaptureDiscussionAsync(Scope scope, CancellationToken ct)
    {
        if (scope.Run.Mode != "collect-only")
            throw new InvalidOperationException("Deferred discussion requires a collect-only run.");
        var existing = store.TryGetLatestArtifact(scope.Run.Id, "workflow-deferred-discussion");
        if (existing is not null)
        {
            var saved = JsonNode.Parse(existing.Payload)!.AsObject();
            if (
                saved["ReviewRunId"]?.GetValue<long>() != scope.Run.Id
                || saved["HeadSha"]?.GetValue<string>() != scope.Run.HeadSha
                || saved["WorkflowInstanceId"]?.GetValue<string>() != scope.InstanceId
            )
                throw new InvalidDataException("Deferred discussion belongs to another review.");
            return saved;
        }
        // Require durable, correlated reports as well as authored step ordering before reading threads.
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(scope.Directory, "private"), "invocation-*.json"))
        {
            var evidence = JsonNode.Parse(await ReadBoundedAsync(path, ct).ConfigureAwait(false))!;
            var invocation = evidence["Invocation"];
            if (
                invocation?["InstanceId"]?.GetValue<string>() != scope.InstanceId
                || evidence["Result"]?["Status"]?.GetValue<int>() != (int)WorkflowInvocationStatus.Completed
            )
                continue;
            var id = invocation["Task"]
                ?["Id"]?.GetValue<string>()
                ?.Replace(":task", string.Empty, StringComparison.Ordinal);
            if (id is "independent-review" or "independent-grade" or "collect-comments")
            {
                var report = JsonNode.Parse(evidence["Result"]!["Output"]!.GetValue<string>());
                if (
                    report?["Format"]?.GetValue<string>() == "markdown"
                    && !string.IsNullOrWhiteSpace(report["Markdown"]?.GetValue<string>())
                )
                    completed.Add(id);
            }
        }
        if (completed.Count != 3)
            throw new InvalidDataException(
                "Review, grade and proposed comments must be persisted before discussion capture."
            );
        var read = readDiscussion ?? throw new InvalidOperationException("Discussion reader is unavailable.");
        var snapshot = await read(scope.Run, ct).ConfigureAwait(false);
        snapshot["ReviewRunId"] = scope.Run.Id;
        snapshot["HeadSha"] = scope.Run.HeadSha;
        snapshot["WorkflowInstanceId"] = scope.InstanceId;
        snapshot["CapturedAt"] = DateTimeOffset.UtcNow.ToString("O");
        var payload = snapshot.ToJsonString();
        if (payload.Length > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidDataException("Complete discussion exceeds the artifact limit.");
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = scope.Run.Id,
                ArtifactKind = "workflow-deferred-discussion",
                ArtifactSchemaVersion = 1,
                Provider = RepoIdentity.ToPublisherNamespace(store.GetRepo(scope.Run.RepoId)!.Provider),
                Payload = payload,
            }
        );
        return snapshot;
    }

    public async Task<JsonObject> RetainCompletedReviewNotesAsync(ReviewRun run, CancellationToken ct)
    {
        if (run.WorkflowStatus != WorkflowStatus.Completed || !artifactBranchCapability().ExportsFullReviewOutput)
            throw new InvalidOperationException(
                "Supplemental retention requires a completed, explicitly authorized run."
            );
        var instance = $"review-run-{run.Id.ToString(CultureInfo.InvariantCulture)}";
        var directory = Path.Combine(trustedRunDirectoryRoot, InstanceDirectoryName(instance));
        var path = Path.Combine(directory, "review-notes-supplement.json");
        var repo = store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Review repository is missing.");
        var branch = ReviewBranchManager.BuildReviewBranchName(repo, int.Parse(run.PrId, CultureInfo.InvariantCulture));
        var binding = ReviewArtifactExportBinding.Build(run, repo, branch);
        ReviewArtifactFile file;
        if (File.Exists(path))
        {
            file =
                JsonSerializer.Deserialize<ReviewArtifactFile>(await ReadBoundedAsync(path, ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException("Invalid supplemental bundle.");
        }
        else
        {
            var notes = await workspace.CaptureReviewNotesAsync(run, instance, ct).ConfigureAwait(false);
            if (notes.Count == 0)
                throw new InvalidOperationException("No review notes remain; nothing to supplement.");
            file = ReviewArtifactExport.BuildReviewNotes(
                ReviewArtifactExport.BuildPrefix(branch, InstanceDirectoryName(instance)),
                binding,
                notes
            );
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, file, cancellationToken: ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        var expectedPath =
            ReviewArtifactExport.BuildPrefix(branch, InstanceDirectoryName(instance)) + "review-notes.json";
        if (file.RelativePath != expectedPath)
            throw new InvalidOperationException("Supplemental retention path does not match this run.");
        ValidatePublicArtifact(file, binding);
        var operations = await artifactOperations(run, ct).ConfigureAwait(false);
        return await operations.RetainArtifactsAsync([file], ct, instance + "/review-notes").ConfigureAwait(false);
    }

    /// <summary>Returns null when durable evidence cannot prove completion; never replays a mutation.</summary>
    public async Task<JsonNode?> ReconcileAsync(
        string operation,
        JsonObject context,
        JsonNode input,
        CancellationToken cancellationToken
    )
    {
        var scope = await ResolveScopeAsync(operation, context, input, cancellationToken).ConfigureAwait(false);
        if (operation.StartsWith("prepare-", StringComparison.Ordinal))
        {
            var prepared = await workspace
                .ReconcilePreparationAsync(scope.Run, scope.Admission, scope.InstanceId, cancellationToken)
                .ConfigureAwait(false);
            return prepared is null ? null : PreparationOutput(operation, scope.Run, prepared);
        }
        if (operation == "collect-publication")
            return CollectedPublication(scope.Run);
        if (operation == "fetch-discussion")
            return await CaptureDiscussionAsync(scope, cancellationToken).ConfigureAwait(false);
        var operations = await artifactOperations(scope.Run, cancellationToken).ConfigureAwait(false);
        if (operation == "collect-statistics")
            return operations.CollectStatistics();
        var files = await TryReadBundleAsync(scope, operation == "retain-artifacts", cancellationToken)
            .ConfigureAwait(false);
        if (files is null)
            return null;
        var retained = operations.ReconcileRetention(scope.InstanceId, files);
        if (operation == "retain-artifacts")
            return retained;
        if (!JsonNode.DeepEquals(retained, input))
            return null;
        return await operations
            .ReconcileClosureAsync(cancellationToken, scope.InstanceId, [.. files.Select(file => file.RelativePath)])
            .ConfigureAwait(false);
    }

    private async Task<JsonNode> CloseAsync(
        WorkflowArtifactOperations operations,
        Scope scope,
        JsonNode input,
        CancellationToken cancellationToken
    )
    {
        var files = await TryReadBundleAsync(scope, checkExtractions: false, cancellationToken).ConfigureAwait(false);
        if (files is null || !JsonNode.DeepEquals(operations.ReconcileRetention(scope.InstanceId, files), input))
            throw new InvalidOperationException(
                "Closure input does not match this workflow's durable retention receipt."
            );
        return await operations
            .CloseArtifactBranchAsync(cancellationToken, scope.InstanceId, [.. files.Select(file => file.RelativePath)])
            .ConfigureAwait(false);
    }

    private async Task<Scope> ResolveScopeAsync(
        string operation,
        JsonObject context,
        JsonNode input,
        CancellationToken cancellationToken
    )
    {
        if (!Supports(operation))
            throw new InvalidOperationException("Unsupported workflow operation.");
        if (
            !long.TryParse(
                context["RunId"]?.GetValue<string>(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var runId
            )
            || runId <= 0
            || context["Attempt"]?.GetValue<int>() is not > 0
            || string.IsNullOrWhiteSpace(context["StepId"]?.GetValue<string>())
        )
            throw new InvalidOperationException("Invalid trusted workflow context.");
        var run = store.GetReviewRun(runId) ?? throw new InvalidOperationException("Workflow run does not exist.");
        var directory = Path.GetFullPath(
            context["RunDirectory"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Run directory is missing.")
        );
        var root = Path.GetFullPath(trustedRunDirectoryRoot);
        if (
            !string.Equals(
                Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)),
                Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        )
            throw new InvalidOperationException(
                "Run directory must be directly contained in the trusted workflow root."
            );
        var relative = Path.GetRelativePath(root, Path.Combine(directory, "scope.json")).Replace('\\', '/');
        var scopePath = WorkflowScriptInvoker.ResolveWorkspaceAsset(relative, root);
        var json =
            JsonNode.Parse(await ReadBoundedAsync(scopePath, cancellationToken).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidOperationException("Invalid persisted workflow scope.");
        var instance = json["WorkflowInstanceId"]?.GetValue<string>();
        var admission = json["Admission"] as JsonObject;
        var frozenContext = json["FrozenContext"] as JsonObject;
        if (
            string.IsNullOrWhiteSpace(instance)
            || json["ReviewRunId"]?.GetValue<long>() != runId
            || admission is null
            || frozenContext is null
            || admission["PrId"]?.GetValue<string>() != run.PrId
            || admission["HeadSha"]?.GetValue<string>() != run.HeadSha
        )
            throw new InvalidOperationException("Persisted workflow scope does not match the trusted run.");
        var expected = Path.Combine(root, InstanceDirectoryName(instance));
        if (
            !string.Equals(
                Path.TrimEndingDirectorySeparator(directory),
                expected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        )
            throw new InvalidOperationException("Run directory does not match the persisted workflow instance.");
        var boundAdmission = operation == "retain-artifacts" ? input["Admission"] : input;
        JsonArray? extractions = operation == "retain-artifacts" ? input["Extractions"] as JsonArray : [];
        var route = admission["Route"]?.GetValue<string>();
        if (
            route is not ("new_head" or "discussion" or "merged")
            || (
                operation is "prepare-review" or "prepare-independent" or "fetch-discussion" or "collect-publication"
                && route != "new_head"
            )
            || (operation == "prepare-discussion" && route != "discussion")
            || (operation is "prepare-history" or "close-artifact-branch" && route != "merged")
            || (operation != "close-artifact-branch" && !JsonNode.DeepEquals(boundAdmission, admission))
            || extractions is null
            || (route != "merged" && extractions.Count != 0)
        )
            throw new InvalidOperationException("Operation input does not match the frozen workflow admission.");
        var review = operation == "retain-artifacts" ? input["KnowledgeReview"] as JsonObject : null;
        if (operation == "retain-artifacts" && route == "merged")
            WorkflowKnowledgeEdits.ValidateReview(extractions, review);
        return new Scope(
            run,
            instance,
            directory,
            admission,
            frozenContext,
            extractions,
            review,
            operation == "retain-artifacts" ? input["Canonical"] as JsonObject : null
        );
    }

    private async Task<IReadOnlyList<ReviewArtifactFile>> ReadOrCaptureBundleAsync(
        Scope scope,
        WorkflowArtifactOperations operations,
        JsonObject? frozenContextDigest,
        CancellationToken cancellationToken
    )
    {
        var capability = artifactBranchCapability();
        var bundlePath = Path.Combine(scope.Directory, "retention-bundle.json");
        if (File.Exists(bundlePath))
        {
            var existing = await ReadBundleRecordAsync(scope, checkExtractions: true, cancellationToken)
                .ConfigureAwait(false);
            if (existing.ExportsFullReviewOutput == capability.ExportsFullReviewOutput)
                return existing.Files;
            throw new InvalidOperationException("Retention bundle export authority does not match this process.");
        }
        var repo = store.GetRepo(scope.Run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        var branch = ReviewBranchManager.BuildReviewBranchName(
            repo,
            int.Parse(scope.Run.PrId, CultureInfo.InvariantCulture)
        );
        var prefix = ReviewArtifactExport.BuildPrefix(branch, InstanceDirectoryName(scope.InstanceId));
        // Public Git receives only fixed host metadata. Invocation payloads and canonical
        // review records remain private; filenames never authorize an export.
        var summary = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["ReviewRunId"] = scope.Run.Id,
            ["Route"] = scope.Admission["Route"]!.DeepClone(),
            ["KnowledgeEntryCount"] = scope.Extractions.Sum(value => (value?["Edits"] as JsonArray)?.Count ?? 0),
        };
        var files = new List<ReviewArtifactFile> { new(prefix + "summary.json", summary.ToJsonString()) };
        // Task #82, requirement 2. Reached ONLY under the pilot grant; the daemon's own posture keeps the
        // canonical records private and this list at exactly one fixed-metadata file.
        if (capability.ExportsFullReviewOutput)
        {
            var notes =
                scope.Admission["Route"]?.GetValue<string>() == "new_head"
                    ? await workspace
                        .CaptureReviewNotesAsync(scope.Run, scope.InstanceId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            files.AddRange(
                ReviewArtifactExport.Build(
                    prefix,
                    scope.Run,
                    repo,
                    branch,
                    scope.Admission,
                    scope.FrozenContext,
                    scope.Canonical,
                    operations.CollectRetentionInventory(),
                    frozenContextDigest
                )
            );
            if (notes is not null)
                files.Add(ReviewArtifactExport.BuildReviewNotes(prefix, BindingFor(scope), notes));
        }
        files.AddRange(
            await operations
                .PrepareKnowledgeFilesAsync(scope.Extractions, cancellationToken, scope.KnowledgeReview)
                .ConfigureAwait(false)
        );
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RetentionBundle(
                1,
                scope.InstanceId,
                scope.Run.Id,
                ExtractionHash(scope.Extractions),
                capability.ExportsFullReviewOutput,
                files
            )
        );
        if (Encoding.UTF8.GetCharCount(bytes) > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Retention bundle exceeds the configured artifact limit.");
        var temporaryPath = bundlePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (
                var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            )
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, bundlePath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
        return files;
    }

    private async Task<IReadOnlyList<ReviewArtifactFile>> ReadBundleAsync(
        Scope scope,
        bool checkExtractions,
        CancellationToken cancellationToken
    ) => (await ReadBundleRecordAsync(scope, checkExtractions, cancellationToken).ConfigureAwait(false)).Files;

    private async Task<RetentionBundle> ReadBundleRecordAsync(
        Scope scope,
        bool checkExtractions,
        CancellationToken cancellationToken
    )
    {
        var path = WorkflowScriptInvoker.ResolveWorkspaceAsset("retention-bundle.json", scope.Directory);
        var bundle = JsonSerializer.Deserialize<RetentionBundle>(
            await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false)
        );
        if (
            bundle is null
            || bundle.ExportSchema != 1
            || bundle.WorkflowInstanceId != scope.InstanceId
            || bundle.ReviewRunId != scope.Run.Id
            || bundle.Files is null
            || (checkExtractions && bundle.ExtractionHash != ExtractionHash(scope.Extractions))
        )
            throw new InvalidOperationException("Retention bundle does not match the trusted workflow scope.");
        if (bundle.ExportsFullReviewOutput && scope.Admission["Route"]?.GetValue<string>() == "new_head")
        {
            var repo =
                store.GetRepo(scope.Run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
            var branch = ReviewBranchManager.BuildReviewBranchName(
                repo,
                int.Parse(scope.Run.PrId, CultureInfo.InvariantCulture)
            );
            var expectedNotesPath =
                ReviewArtifactExport.BuildPrefix(branch, InstanceDirectoryName(scope.InstanceId)) + "review-notes.json";
            if (bundle.Files.Count(file => file.RelativePath == expectedNotesPath) != 1)
                throw new InvalidOperationException("Retention bundle is missing its bound review notes.");
        }
        foreach (var file in bundle.Files.Where(file => file.RelativePath.StartsWith("PRs/", StringComparison.Ordinal)))
            ValidatePublicArtifact(file, BindingFor(scope));
        return bundle;
    }

    /// <summary>
    /// The identity a replayed bundle's files must claim. Rebuilt from the CURRENT scope rather than read
    /// from the bundle, which is the whole point: a file that names a different run does not get to define
    /// what it is compared against.
    /// </summary>
    private ReviewArtifactExportBinding BindingFor(Scope scope)
    {
        var repo = store.GetRepo(scope.Run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        return ReviewArtifactExportBinding.Build(
            scope.Run,
            repo,
            ReviewBranchManager.BuildReviewBranchName(repo, int.Parse(scope.Run.PrId, CultureInfo.InvariantCulture))
        );
    }

    private async Task<IReadOnlyList<ReviewArtifactFile>?> TryReadBundleAsync(
        Scope scope,
        bool checkExtractions,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await ReadBundleAsync(scope, checkExtractions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    // Compatibility projections stay in the private store. Draft text and publication are
    // explicitly distinct; per-finding support does not invent a historical numeric grade.
    private JsonObject? PersistCanonical(Scope scope)
    {
        if (scope.Admission["Route"]?.GetValue<string>() != "new_head" || scope.Canonical is null)
            return null;
        var canonical = scope.Canonical;
        var review =
            canonical["Review"] as JsonObject ?? throw new InvalidOperationException("Canonical review is missing.");
        var grade =
            canonical["Grade"] as JsonObject ?? throw new InvalidOperationException("Canonical grade is missing.");
        if (review["Format"]?.GetValue<string>() == "markdown")
        {
            Save(
                ReviewArtifactKinds.ReviewArtifactKind,
                ReviewArtifactKinds.ReviewArtifactSchemaVersion,
                new JsonObject
                {
                    ["ReviewText"] = review["Markdown"]!.DeepClone(),
                    ["TextKind"] = "markdown",
                    ["VariantId"] = scope.Run.VariantId,
                    ["Publication"] = canonical["Publication"]?.DeepClone(),
                }
            );
            Save(
                ReviewArtifactKinds.JudgeArtifactKind,
                ReviewArtifactKinds.JudgeArtifactSchemaVersion,
                new JsonObject
                {
                    ["Score"] = null,
                    ["Rationale"] = grade["Markdown"]!.DeepClone(),
                    ["Markdown"] = grade["Markdown"]!.DeepClone(),
                    ["GradeKind"] = "markdown-support",
                    ["VariantId"] = scope.Run.VariantId,
                }
            );
            Save("workflow-comments", 1, canonical["Comments"]!.DeepClone());
            Save("workflow-performance", 1, canonical["Performance"]!.DeepClone());
        }
        else
        {
            var findings =
                review["Findings"] as JsonArray
                ?? throw new InvalidOperationException("Canonical findings are missing.");
            var reviewPayload = JsonSerializer
                .SerializeToNode(
                    new ReviewArtifactPayload(review["ReviewText"]!.GetValue<string>(), null, scope.Run.VariantId)
                )!
                .AsObject();
            reviewPayload["TextKind"] = "validated-draft";
            reviewPayload["Publication"] = canonical["Publication"]?.DeepClone();
            Save(
                ReviewArtifactKinds.ReviewArtifactKind,
                ReviewArtifactKinds.ReviewArtifactSchemaVersion,
                reviewPayload
            );
            var records = new JsonArray();
            foreach (var finding in findings)
            {
                var record = JsonSerializer
                    .SerializeToNode(
                        new ReviewFindingRecord(
                            "workflow-review",
                            "workflow",
                            finding!["Description"]!.GetValue<string>(),
                            finding["Path"]!.GetValue<string>()
                                + ":"
                                + finding["Line"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture),
                            finding["Severity"]!.GetValue<string>(),
                            [finding["Severity"]!.GetValue<string>()],
                            "uncompared",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null
                        )
                    )!
                    .AsObject();
                record["Id"] = finding["Id"]!.DeepClone();
                records.Add(record);
            }
            Save(
                ReviewArtifactKinds.FindingsArtifactKind,
                ReviewArtifactKinds.FindingsArtifactSchemaVersion,
                new JsonObject
                {
                    ["Round"] = 1,
                    ["DerivedFrom"] = "validated-workflow-draft",
                    ["Compared"] = false,
                    ["ParsedCount"] = findings.Count,
                    ["RecordedCount"] = findings.Count,
                    ["Sources"] = new JsonArray(),
                    ["Findings"] = records,
                }
            );
            var judge = JsonSerializer
                .SerializeToNode(
                    new JudgeArtifactPayload(
                        null,
                        grade["Description"]!.GetValue<string>(),
                        scope.Run.VariantId,
                        null,
                        null,
                        null,
                        0
                    )
                )!
                .AsObject();
            judge["Assessments"] = grade["Assessments"]!.DeepClone();
            judge["GradeKind"] = "per-finding-support";
            Save(ReviewArtifactKinds.JudgeArtifactKind, ReviewArtifactKinds.JudgeArtifactSchemaVersion, judge);
        }
        JsonObject? digest = null;
        var evidence = store.TryGetLatestArtifact(scope.Run.Id, "workflow-diff");
        if (evidence is not null)
        {
            var diff = JsonNode.Parse(evidence.Payload)!;
            if (diff["HeadSha"]?.GetValue<string>() != scope.Run.HeadSha)
                throw new InvalidOperationException("Canonical context diff does not match the run head.");
            // The digest, never the body: it proves WHICH diff this review was frozen against without
            // copying the reviewed source into the artifact repository (task #82, requirement 2).
            var body = diff["Diff"]!.GetValue<string>();
            digest = new JsonObject
            {
                ["Sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
                ["Length"] = body.Length,
                ["MergeBaseSha"] = diff["BaseSha"]?.DeepClone(),
            };
            Save(
                ReviewArtifactKinds.ContextArtifactKind,
                ReviewArtifactKinds.ContextArtifactSchemaVersion,
                JsonSerializer.SerializeToNode(
                    new ContextArtifactPayload(
                        scope.Run.PrId,
                        scope.Run.BaseSha,
                        scope.Run.HeadSha,
                        diff["Diff"]!.GetValue<string>(),
                        MergeBaseSha: diff["BaseSha"]?.GetValue<string>()
                    )
                )!
            );
        }

        return digest;

        void Save(string kind, int version, JsonNode payload)
        {
            var serialized = payload.ToJsonString();
            if (store.TryGetLatestArtifact(scope.Run.Id, kind)?.Payload == serialized)
                return;
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = scope.Run.Id,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = version,
                    Provider = RepoIdentity.ToPublisherNamespace(store.GetRepo(scope.Run.RepoId)!.Provider),
                    Payload = serialized,
                }
            );
        }
    }

    internal static void ValidatePublicArtifact(ReviewArtifactFile file, ReviewArtifactExportBinding binding)
    {
        // Task #82, requirement 2 — the export widens what MAY reach public git, and the allow-list widens
        // with it by an explicitly enumerated set of file names, never by pattern. A name outside both is
        // still refused, and an un-granted daemon cannot push at all, so the wider set is unreachable
        // without the pilot capability.
        var fileName = file.RelativePath[(file.RelativePath.LastIndexOf('/') + 1)..];
        if (ReviewArtifactExport.IsExportFileName(fileName))
        {
            ReviewArtifactExport.ValidateExportedArtifact(file, fileName, binding);
            return;
        }
        var summary = JsonNode.Parse(file.Content) as JsonObject;
        if (
            !file.RelativePath.EndsWith("/summary.json", StringComparison.Ordinal)
            || summary is null
            || summary.Count != 4
            || summary["SchemaVersion"]?.GetValue<int>() != 1
            || summary["ReviewRunId"]?.GetValue<long>() != binding.ReviewRunId
            || summary["Route"]?.GetValue<string>() is not ("new_head" or "discussion" or "merged")
            || summary["KnowledgeEntryCount"]?.GetValue<int>() is not >= 0
        )
            throw new InvalidOperationException("Public retention permits only the fixed metadata summary schema.");
    }

    private static string ExtractionHash(JsonArray extractions) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(extractions.ToJsonString())));

    private int MaximumReadBytes => checked(options.Limits.MaxArtifactPayloadChars * 4);

    private async Task<string> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumReadBytes)
            throw new InvalidOperationException("Workflow artifact exceeds the configured size limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[options.Limits.MaxArtifactPayloadChars + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (count > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Workflow artifact exceeds the configured size limit.");
        return new string(buffer, 0, count);
    }

    private static string InstanceDirectoryName(string instanceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)));
}
