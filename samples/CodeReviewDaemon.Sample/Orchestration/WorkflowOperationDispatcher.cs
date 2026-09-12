using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
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
        JsonArray Extractions
    );

    private sealed record RetentionBundle(
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
        return new Scope(run, instance, directory, admission, extractions);
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
        var artifactsPath = WorkflowScriptInvoker.ResolveWorkspaceAsset("artifacts", scope.Directory);
        var files = new List<ReviewArtifactFile>();
        long capturedCharacters = 0;
        foreach (
            var path in Directory
                .EnumerateFiles(artifactsPath, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
        )
        {
            var name = Path.GetFileName(path);
            var safePath = WorkflowScriptInvoker.ResolveWorkspaceAsset("artifacts/" + name, scope.Directory);
            var content = await ReadBoundedAsync(safePath, cancellationToken).ConfigureAwait(false);
            capturedCharacters += content.Length;
            if (capturedCharacters > options.Limits.MaxArtifactPayloadChars)
                throw new InvalidOperationException("Retention bundle exceeds the configured artifact limit.");
            _ = JsonNode.Parse(content) ?? throw new InvalidOperationException("Retained artifact must contain JSON.");
            files.Add(new ReviewArtifactFile(prefix + name, content));
        }
        if (files.Count == 0)
            throw new InvalidOperationException("Required retention cannot contain zero artifacts.");
        files.AddRange(
            await operations.PrepareKnowledgeFilesAsync(scope.Extractions, cancellationToken).ConfigureAwait(false)
        );
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RetentionBundle(scope.InstanceId, scope.Run.Id, ExtractionHash(scope.Extractions), files)
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
            || bundle.WorkflowInstanceId != scope.InstanceId
            || bundle.ReviewRunId != scope.Run.Id
            || bundle.Files is null
            || (checkExtractions && bundle.ExtractionHash != ExtractionHash(scope.Extractions))
        )
            throw new InvalidOperationException("Retention bundle does not match the trusted workflow scope.");
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
