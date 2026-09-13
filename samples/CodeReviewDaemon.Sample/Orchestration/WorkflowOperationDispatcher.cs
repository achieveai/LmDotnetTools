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
    Func<ReviewRun, CancellationToken, Task<WorkflowArtifactOperations>> artifactOperations
)
{
    private sealed record Scope(
        ReviewRun Run,
        string InstanceId,
        string Directory,
        JsonObject Admission,
        JsonArray Extractions,
        JsonObject? KnowledgeReview,
        JsonObject? Canonical
    );

    private sealed record RetentionBundle(
        int ExportSchema,
        string WorkflowInstanceId,
        long ReviewRunId,
        string ExtractionHash,
        IReadOnlyList<ReviewArtifactFile> Files
    );

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
            return (
                await workspace
                    .PrepareAssignedAsync(scope.Run, scope.Admission, scope.InstanceId, cancellationToken)
                    .ConfigureAwait(false)
            ).Output;
        }
        if (operation == "retain-artifacts")
            PersistCanonical(scope);
        var operations = await artifactOperations(scope.Run, cancellationToken).ConfigureAwait(false);
        return operation switch
        {
            "collect-statistics" => operations.CollectStatistics(),
            "retain-artifacts" => await operations
                .RetainArtifactsAsync(
                    await ReadOrCaptureBundleAsync(scope, operations, cancellationToken).ConfigureAwait(false),
                    cancellationToken,
                    scope.InstanceId
                )
                .ConfigureAwait(false),
            "close-artifact-branch" => await CloseAsync(operations, scope, input, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported workflow operation."),
        };
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
            return await workspace
                .ReconcilePreparationAsync(scope.Run, scope.Admission, scope.InstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
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
        if (
            operation
            is not (
                "prepare-review"
                or "prepare-discussion"
                or "prepare-history"
                or "collect-statistics"
                or "retain-artifacts"
                or "close-artifact-branch"
            )
        )
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
        if (
            string.IsNullOrWhiteSpace(instance)
            || json["ReviewRunId"]?.GetValue<long>() != runId
            || admission is null
            || json["FrozenContext"] is not JsonObject
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
            || (operation == "prepare-review" && route != "new_head")
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
            extractions,
            review,
            operation == "retain-artifacts" ? input["Canonical"] as JsonObject : null
        );
    }

    private async Task<IReadOnlyList<ReviewArtifactFile>> ReadOrCaptureBundleAsync(
        Scope scope,
        WorkflowArtifactOperations operations,
        CancellationToken cancellationToken
    )
    {
        var bundlePath = Path.Combine(scope.Directory, "retention-bundle.json");
        if (File.Exists(bundlePath))
            return await ReadBundleAsync(scope, checkExtractions: true, cancellationToken).ConfigureAwait(false);
        var repo = store.GetRepo(scope.Run.RepoId) ?? throw new InvalidOperationException("Run repository is missing.");
        var branch = ReviewBranchManager.BuildReviewBranchName(
            repo,
            int.Parse(scope.Run.PrId, CultureInfo.InvariantCulture)
        );
        var prefix = $"PRs/{branch["review/".Length..]}/{InstanceDirectoryName(scope.InstanceId)}/";
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
        files.AddRange(
            await operations
                .PrepareKnowledgeFilesAsync(scope.Extractions, cancellationToken, scope.KnowledgeReview)
                .ConfigureAwait(false)
        );
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RetentionBundle(1, scope.InstanceId, scope.Run.Id, ExtractionHash(scope.Extractions), files)
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
        foreach (var file in bundle.Files.Where(file => file.RelativePath.StartsWith("PRs/", StringComparison.Ordinal)))
            ValidatePublicArtifact(file, scope.Run.Id);
        return bundle.Files;
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
    private void PersistCanonical(Scope scope)
    {
        if (scope.Admission["Route"]?.GetValue<string>() != "new_head" || scope.Canonical is null)
            return;
        var canonical = scope.Canonical;
        var review =
            canonical["Review"] as JsonObject ?? throw new InvalidOperationException("Canonical review is missing.");
        var grade =
            canonical["Grade"] as JsonObject ?? throw new InvalidOperationException("Canonical grade is missing.");
        var findings =
            review["Findings"] as JsonArray ?? throw new InvalidOperationException("Canonical findings are missing.");
        var reviewPayload = JsonSerializer
            .SerializeToNode(
                new ReviewArtifactPayload(review["ReviewText"]!.GetValue<string>(), null, scope.Run.VariantId)
            )!
            .AsObject();
        reviewPayload["TextKind"] = "validated-draft";
        reviewPayload["Publication"] = canonical["Publication"]?.DeepClone();
        Save(ReviewArtifactKinds.ReviewArtifactKind, ReviewArtifactKinds.ReviewArtifactSchemaVersion, reviewPayload);
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
        var evidence = store.TryGetLatestArtifact(scope.Run.Id, "workflow-diff");
        if (evidence is not null)
        {
            var diff = JsonNode.Parse(evidence.Payload)!;
            if (diff["HeadSha"]?.GetValue<string>() != scope.Run.HeadSha)
                throw new InvalidOperationException("Canonical context diff does not match the run head.");
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

    internal static void ValidatePublicArtifact(ReviewArtifactFile file, long runId)
    {
        var summary = JsonNode.Parse(file.Content) as JsonObject;
        if (
            !file.RelativePath.EndsWith("/summary.json", StringComparison.Ordinal)
            || summary is null
            || summary.Count != 4
            || summary["SchemaVersion"]?.GetValue<int>() != 1
            || summary["ReviewRunId"]?.GetValue<long>() != runId
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
