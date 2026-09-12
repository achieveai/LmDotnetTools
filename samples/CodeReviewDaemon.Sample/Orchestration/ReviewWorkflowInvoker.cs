using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Production composition of trusted scripts and hosted agent sessions, with correlated output evidence.</summary>
internal sealed class ReviewWorkflowInvoker(
    ReviewRun run,
    string instanceId,
    string runDirectory,
    string packageRoot,
    WorkflowScriptInvoker scripts,
    WorkflowOperationDispatcher dispatcher,
    LmStreamingS2SClient client,
    WorkflowWorkspace workspace,
    ReviewStore store,
    CodeReviewDaemonOptions options,
    WorkflowPublicationScopes publicationScopes,
    Func<ReviewRun, string, CancellationToken, Task<ReviewPublicationTools>> scopeFactory,
    ILoggerFactory loggerFactory
) : IWorkflowTaskInvoker
{
    private ReviewPublicationTools? _publicationTools;

    private sealed record SessionBinding(
        string SessionId,
        string ThreadId,
        string WorkspaceId,
        string ModeId,
        int PublicationPolicyVersion
    );

    private sealed record RawResult(int SchemaVersion, WorkflowInvocation Invocation, WorkflowInvocationResult Result);

    public Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default) =>
        ExecuteAsync(invocation, false, ct);

    public Task<WorkflowInvocationResult> ReconcileAsync(
        WorkflowInvocation invocation,
        CancellationToken ct = default
    ) => ExecuteAsync(invocation, true, ct);

    private async Task<WorkflowInvocationResult> ExecuteAsync(
        WorkflowInvocation invocation,
        bool reconcile,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.InstanceId != instanceId)
            return new(WorkflowInvocationStatus.Failed, Error: "Invocation does not belong to this workflow instance.");
        try
        {
            var evidence = await ReadResultAsync(invocation, ct).ConfigureAwait(false);
            if (evidence is { Status: WorkflowInvocationStatus.Completed or WorkflowInvocationStatus.Failed })
                return evidence;
            if (invocation.Task.Delegate == DelegateKind.Agent && invocation.DeadlineUtc is null)
                return new(WorkflowInvocationStatus.Failed, Error: "Agent invocation requires a persisted deadline.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!reconcile && invocation.DeadlineUtc is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return new(
                        WorkflowInvocationStatus.Failed,
                        Error: "The invocation deadline elapsed before dispatch."
                    );
                budget.CancelAfter(remaining);
                ct = budget.Token;
            }

            WorkflowInvocationResult result;
            if (invocation.Task.Delegate == DelegateKind.Agent)
            {
                var agent = new WorkflowAgentInvoker(packageRoot, ResolveSessionAsync, SettleAsync);
                result = reconcile
                    ? await agent.ReconcileAsync(invocation, ct).ConfigureAwait(false)
                    : await agent.InvokeAsync(invocation, ct).ConfigureAwait(false);
            }
            else if (
                invocation.Task.Delegate == DelegateKind.Script
                && !string.IsNullOrWhiteSpace(invocation.Task.Script)
                && !invocation.IsCorrection
            )
            {
                var context = ScriptContext(invocation);
                if (reconcile)
                {
                    var proof = await dispatcher
                        .ReconcileAsync(
                            Path.GetFileNameWithoutExtension(invocation.Task.Script),
                            context,
                            invocation.Input,
                            ct
                        )
                        .ConfigureAwait(false);
                    result = proof is null
                        ? new(
                            WorkflowInvocationStatus.Unknown,
                            Error: "No durable proof confirms this script invocation."
                        )
                        : new(WorkflowInvocationStatus.Completed, proof.ToJsonString());
                }
                else
                {
                    try
                    {
                        var output = await scripts
                            .InvokeAsync(invocation.Task.Script, packageRoot, context, invocation.Input, ct)
                            .ConfigureAwait(false);
                        result = new(WorkflowInvocationStatus.Completed, output);
                    }
                    catch (InvalidOperationException ex) when (ex is not WorkflowScriptTerminationException)
                    {
                        result = new(
                            HasUnresolvedReceipt("workflow-")
                                ? WorkflowInvocationStatus.Unknown
                                : WorkflowInvocationStatus.Failed,
                            Error: ex.Message
                        );
                    }
                }
            }
            else
                result = new(WorkflowInvocationStatus.Failed, Error: "Unsupported workflow invocation delegate.");

            await SaveResultAsync(invocation, result, ct).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or HttpRequestException
                        or OperationCanceledException
                        or InvalidOperationException
                        or ArgumentException
                        or JsonException
            )
        {
            // A missing response/evidence does not authorize replaying a script that may have committed
            // an external effect. Reconciliation must recover its receipt or keep the invocation blocked.
            return new(WorkflowInvocationStatus.Unknown, Error: ex.Message);
        }
    }

    private async Task<S2SReviewAgent?> ResolveSessionAsync(
        WorkflowInvocation invocation,
        bool allowCreate,
        CancellationToken ct
    )
    {
        var session = invocation.SessionId ?? throw new InvalidOperationException("Agent session identity is missing.");
        var kind = "workflow-session:" + Hash(session);
        var artifact = store.TryGetLatestArtifact(run.Id, kind);
        if (artifact is null && !allowCreate)
            return null;
        var prepared = workspace.ReadPreparation(run).Workspace;
        var requestedModel = string.IsNullOrWhiteSpace(invocation.Task.ModelId) ? null : invocation.Task.ModelId;
        if (
            artifact is not null
            && requestedModel is not null
            && !string.Equals(artifact.Provider, requestedModel, StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException(
                "The authored model conflicts with the persisted parent session model."
            );
        var providerId =
            artifact?.Provider
            ?? requestedModel
            ?? (string.IsNullOrWhiteSpace(run.ModelId) ? options.LmStreamingProviderId : run.ModelId);
        await client.EnsureWorkflowPublicationAsync(ct, providerId, options.LmStreamingModeId).ConfigureAwait(false);
        SessionBinding binding;
        if (artifact is null)
        {
            var threadId = await client
                .ProvisionAsync(
                    prepared.WorkspaceId,
                    providerId,
                    options.LmStreamingModeId,
                    systemPromptAppendix: null,
                    options.SubAgentModelId,
                    options.ToolAssistedReasoningEffort,
                    ct
                )
                .ConfigureAwait(false);
            binding = new(session, threadId, prepared.WorkspaceId, options.LmStreamingModeId, 1);
            // Provision has no model turn. Persist its identity before the adapter may enqueue one.
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = 1,
                    Provider = providerId,
                    Payload = JsonSerializer.Serialize(binding),
                }
            );
            if (options.DeepLinkRetentionHours > 0)
                store.RecordDeepLinkConversation(
                    threadId,
                    $"Review PR #{run.PrId}: {invocation.Task.Label ?? invocation.Task.Id}"
                );
        }
        else
        {
            binding =
                JsonSerializer.Deserialize<SessionBinding>(artifact.Payload)
                ?? throw new InvalidOperationException("Persisted agent session is invalid.");
            if (
                binding.SessionId != session
                || binding.WorkspaceId != prepared.WorkspaceId
                || string.IsNullOrWhiteSpace(binding.ThreadId)
                || binding.PublicationPolicyVersion != 1
                || binding.ModeId != options.LmStreamingModeId
            )
                throw new InvalidOperationException("Persisted agent session does not match the prepared workspace.");
        }

        var tools = await scopeFactory(run, instanceId, ct).ConfigureAwait(false);
        _publicationTools = tools;
        publicationScopes.Register(binding.ThreadId, invocation, tools);
        return new S2SReviewAgent(
            client,
            prepared.WorkspaceId,
            providerId,
            options.LmStreamingModeId,
            systemPrompt: null,
            title: null,
            loggerFactory.CreateLogger<S2SReviewAgent>(),
            overallTimeout: TimeSpan.FromMinutes(options.ReviewStageDeadlineMinutes),
            existingThreadId: binding.ThreadId,
            subAgentModelId: options.SubAgentModelId,
            reasoningEffort: options.ToolAssistedReasoningEffort
        );
    }

    private async Task<WorkflowInvocationResult?> SettleAsync(
        WorkflowInvocation invocation,
        string threadId,
        CancellationToken ct
    )
    {
        await (_publicationTools ?? throw new InvalidOperationException("Publication receipt scope is unavailable."))
            .ReconcileAsync(ct)
            .ConfigureAwait(false);
        if (HasUnresolvedReceipt("workflow-publication"))
            return new(WorkflowInvocationStatus.Unknown, Error: "A publication receipt is unresolved.");
        var barrier = new ReviewSubAgentCompletionBarrier(
            new S2SReviewSubAgentCompletionSource(client),
            TimeSpan.FromSeconds(options.ReviewSubAgentBarrierQuietSeconds),
            loggerFactory.CreateLogger<ReviewSubAgentCompletionBarrier>(),
            unknownQuiescence: TimeSpan.Zero
        );
        var deadline =
            invocation.DeadlineUtc
            ?? throw new InvalidOperationException("Agent invocation requires a persisted deadline.");
        if (deadline <= DateTimeOffset.UtcNow)
        {
            if (await barrier.TryConfirmSettledAsync(run, threadId, ct).ConfigureAwait(false) is null)
                return new(
                    WorkflowInvocationStatus.Unknown,
                    Error: "Descendant work remains unresolved after the invocation deadline."
                );
        }
        else
            await barrier.WaitAsync(run, threadId, deadline, _ => Task.CompletedTask, ct).ConfigureAwait(false);
        return HasUnresolvedReceipt("workflow-publication")
            ? new(WorkflowInvocationStatus.Unknown, Error: "A publication receipt is unresolved.")
            : null;
    }

    private bool HasUnresolvedReceipt(string kindPrefix) =>
        store
            .GetOutboxForRun(run.Id)
            .Any(entry =>
                entry.ArtifactKind.StartsWith(kindPrefix, StringComparison.Ordinal)
                && entry.Status is not (OutboxStatus.Posted or OutboxStatus.Sent or OutboxStatus.Collected)
            );

    private JsonObject ScriptContext(WorkflowInvocation invocation) =>
        new()
        {
            ["RunId"] = run.Id.ToString(CultureInfo.InvariantCulture),
            ["StepId"] = invocation.Task.Id,
            ["Attempt"] = invocation.Attempt,
            ["RunDirectory"] = Path.GetFullPath(runDirectory),
        };

    private string ResultPath(WorkflowInvocation invocation) =>
        WorkflowScriptInvoker.ResolveWorkspaceAsset(
            "artifacts/invocation-" + Hash(invocation.InvocationId) + ".json",
            Path.GetFullPath(runDirectory)
        );

    private async Task<WorkflowInvocationResult?> ReadResultAsync(WorkflowInvocation invocation, CancellationToken ct)
    {
        var path = ResultPath(invocation);
        if (!File.Exists(path))
            return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > checked(options.Limits.MaxArtifactPayloadChars * 4L))
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        using var reader = new StreamReader(stream);
        var buffer = new char[options.Limits.MaxArtifactPayloadChars + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (count > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        var saved = JsonSerializer.Deserialize<RawResult>(new string(buffer, 0, count));
        if (
            saved is null
            || saved.SchemaVersion != 1
            || saved.Result is null
            || !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(saved.Invocation),
                JsonSerializer.SerializeToNode(invocation)
            )
        )
            throw new InvalidOperationException("Invocation artifact identity does not match the active invocation.");
        return saved.Result;
    }

    private Task SaveResultAsync(WorkflowInvocation invocation, WorkflowInvocationResult result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new RawResult(1, invocation, result));
        if (json.Length > options.Limits.MaxArtifactPayloadChars)
            throw new InvalidOperationException("Invocation artifact exceeds the configured limit.");
        var bytes = Encoding.UTF8.GetBytes(json);
        return AtomicFile.WriteAsync(
            ResultPath(invocation),
            async (temporary, token) =>
            {
                await using var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                );
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            },
            ct
        );
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
