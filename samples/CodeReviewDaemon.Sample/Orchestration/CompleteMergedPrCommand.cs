using System.Globalization;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Admits one branch-backed completion; never creates a historical completed review.</summary>
internal static class CompleteMergedPrCommand
{
    public static async Task<JsonObject> RunAsync(
        ScopedCommandLineResult command,
        CodeReviewDaemonOptions options,
        ReviewStore store,
        GitRunner git,
        IReadOnlyList<IPrProvider> providers,
        IReadOnlyList<IReviewCommentReader> readers,
        ReviewWorkflowRunner runner,
        CodeReviewDaemon.Sample.Agents.LmStreamingS2SClient client,
        CodeReviewDaemon.Sample.Workspace.Sandbox.HostGitPushAuthorization pushAuthorization,
        ILogger logger,
        CancellationToken ct
    )
    {
        if (options.EnablePrPolling || options.EnableCommentPosting || options.EnableGitPush)
            throw new InvalidOperationException("Merged completion requires polling and broad publication disabled.");
        var target = PrPollTargetBuilder
            .Build(options, logger)
            .Where(value =>
                string.Equals(value.Repo.DisplayName, command.RunPrRepoKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Repo.NormalizedKey, command.RunPrRepoKey, StringComparison.OrdinalIgnoreCase)
            )
            .GroupBy(value => value.Repo.NormalizedKey)
            .Select(group => group.First())
            .Single();
        return await RunTerminalAsync(
            target,
            command.RunPrId!,
            PrLifecycleState.Merged,
            command.ArtifactTipSha!,
            command.LandedSha,
            command.ParentSha,
            options,
            store,
            git,
            providers,
            readers,
            runner,
            pushAuthorization,
            ct,
            command
        );
    }

    internal static async Task ProcessInventoryAsync(
        IReadOnlyList<ReviewBranchInventoryEntry> entries,
        IReadOnlyList<PrPollTarget> targets,
        CodeReviewDaemonOptions options,
        ReviewStore store,
        GitRunner git,
        IReadOnlyList<IPrProvider> providers,
        IReadOnlyList<IReviewCommentReader> readers,
        ReviewWorkflowRunner runner,
        PrOrchestrator orchestrator,
        IReviewSessionProvisioner sessions,
        CodeReviewDaemon.Sample.Workspace.Sandbox.HostGitPushAuthorization pushAuthorization,
        ILogger logger,
        CancellationToken ct,
        Func<
            ReviewRun,
            WorkflowRound,
            Func<CancellationToken, Task>,
            CancellationToken,
            Task<JsonObject?>
        >? finishClosure = null
    )
    {
        var inventory = entries.ToList();
        foreach (var run in store.ListReviewRunsWithArtifact("workflow-content-integration"))
        {
            try
            {
                var repo = store.GetRepo(run.RepoId);
                var round = store.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Merged);
                if (
                    repo is null
                    || run.VariantId != "terminal"
                    || run.PrLifecycleState != PrLifecycleState.Merged
                    || round is not { Outcome: WorkflowRoundOutcome.Failed }
                    || store.TryGetLatestArtifact(run.Id, "workflow-terminal-closure-finished") is not null
                    || inventory.Any(value => value.RepositoryKey == repo.NormalizedKey && value.PrId == run.PrId)
                )
                    continue;
                var proof = JsonNode.Parse(store.TryGetLatestArtifact(run.Id, "workflow-content-integration")!.Payload);
                var frozen = JsonNode.Parse(round.FrozenInputJson);
                var branch = ReviewBranchManager.BuildReviewBranchName(
                    repo,
                    int.Parse(run.PrId, CultureInfo.InvariantCulture)
                );
                var retainedSha = proof?["RetainedSha"]?.GetValue<string>();
                if (
                    proof?["WorkflowInstanceId"]?.GetValue<string>() != round.WorkflowInstanceId
                    || proof?["ArtifactBranch"]?.GetValue<string>() != branch
                    || !IsSha(retainedSha)
                )
                    continue;
                inventory.Add(
                    new ReviewBranchInventoryEntry(
                        branch,
                        retainedSha!,
                        repo.DisplayName,
                        run.PrId,
                        "Merged",
                        "discovered"
                    )
                    {
                        RepositoryKey = repo.NormalizedKey,
                        SourceHeadSha = run.HeadSha,
                        SourceBaseSha = run.BaseSha,
                        MergeCommitSha = run.MergeSha,
                        TargetRefName = frozen?["Merge"]?["TargetRefName"]?.GetValue<string>(),
                    }
                );
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Terminal closure discovery held {RunId} after {ErrorType}; unrelated terminal intake continues.",
                    run.Id,
                    ex.GetType().Name
                );
            }
        }
        foreach (
            var entry in inventory.Where(value =>
                value.Status == "discovered" && value.Lifecycle is "Merged" or "Abandoned"
            )
        )
        {
            var target = targets.FirstOrDefault(value => value.Repo.NormalizedKey == entry.RepositoryKey);
            if (
                target is null
                || entry.PrId is null
                || !int.TryParse(entry.PrId, out var number)
                || number <= 0
                || entry.Branch != ReviewBranchManager.BuildReviewBranchName(target.Repo, number)
                || !IsSha(entry.TipSha)
                || inventory.Count(value => value.RepositoryKey == entry.RepositoryKey && value.PrId == entry.PrId) != 1
            )
                continue;
            try
            {
                var result = await orchestrator.RunTerminalAsync(
                    target.Repo,
                    entry.PrId,
                    async token =>
                    {
                        var provider = providers.Single(value => value.Provider == target.Provider);
                        var current = await provider.GetPullRequestAsync(target.Repo, entry.PrId, token);
                        var lifecycle =
                            entry.Lifecycle == "Merged" ? PrLifecycleState.Merged : PrLifecycleState.Abandoned;
                        if (
                            current is null
                            || current.LifecycleState != lifecycle
                            || current.HeadSha != entry.SourceHeadSha
                            || current.BaseSha != entry.SourceBaseSha
                            || current.TargetRefName != entry.TargetRefName
                            || (lifecycle == PrLifecycleState.Merged && current.MergeCommitSha != entry.MergeCommitSha)
                        )
                            throw new InvalidDataException("Terminal inventory source identity changed.");
                        string? parent = null;
                        var landed = lifecycle == PrLifecycleState.Merged ? current.MergeCommitSha : null;
                        var retainedClosure = store
                            .ListReviewRunsWithArtifact("workflow-content-integration")
                            .LastOrDefault(run =>
                                run.RepoId == store.EnsureRepo(target.Repo)
                                && run.PrId == entry.PrId
                                && run.VariantId == "terminal"
                                && run.HeadSha == current.HeadSha
                                && run.BaseSha == current.BaseSha
                                && run.MergeSha == landed
                                && store.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Merged)
                                    is { Outcome: WorkflowRoundOutcome.Failed }
                            );
                        if (retainedClosure is not null)
                        {
                            var retainedRound = store.GetLatestWorkflowRound(
                                retainedClosure.Id,
                                WorkflowRoundKind.Merged
                            )!;
                            parent = JsonNode
                                .Parse(retainedRound.FrozenInputJson)
                                ?["Merge"]?["ParentSha"]?.GetValue<string>();
                            if (!IsSha(parent))
                                throw new InvalidDataException("Confirmed integration has no frozen landed parent.");
                        }
                        else if (lifecycle == PrLifecycleState.Merged)
                        {
                            if (!IsSha(landed))
                                throw new InvalidDataException("Terminal source has no exact landed commit.");
                            var session = await sessions.GetOrCreateSharedAsync(token);
                            var script = await File.ReadAllTextAsync(
                                Path.Combine(AppContext.BaseDirectory, ".review", "scripts", "read-merged-evidence.py"),
                                token
                            );
                            var request = new JsonObject
                            {
                                ["Repository"] = target.Repo.RepoName,
                                ["LandedSha"] = landed,
                                ["MaximumCharacters"] = 4096,
                                ["Acquire"] = true,
                                ["AcquisitionTimeoutSeconds"] = 120,
                            };
                            var read = await session.CommandRunner.RunAsync(
                                new Workspace.Sandbox.SandboxCommand(
                                    ["python3", "-c", script, request.ToJsonString(), "probe"],
                                    "/workspace"
                                ),
                                token
                            );
                            if (!read.Succeeded || read.Stdout.Length > 4096)
                                throw new InvalidDataException("Terminal landed probe is unavailable.");
                            var proof = JsonNode.Parse(read.Stdout) as JsonObject;
                            parent = proof?["ParentSha"]?.GetValue<string>();
                            if (
                                !IsSha(parent)
                                || proof?["LandedSha"]?.GetValue<string>() != landed
                                || proof?["Repository"]?.GetValue<string>() != target.Repo.RepoName
                            )
                                throw new InvalidDataException(
                                    "Terminal landed probe differs from the source identity."
                                );
                        }
                        return await RunTerminalAsync(
                            target,
                            entry.PrId,
                            lifecycle,
                            entry.TipSha,
                            landed,
                            parent,
                            options,
                            store,
                            git,
                            providers,
                            readers,
                            runner,
                            pushAuthorization,
                            token,
                            orchestrator: orchestrator,
                            finishClosure: finishClosure
                        );
                    },
                    ct
                );
                logger.LogInformation(
                    "Terminal PR {PrId} outcome {Outcome}.",
                    entry.PrId,
                    result["Outcome"]?.GetValue<string>()
                );
                if (result["Outcome"]?.GetValue<string>() is not ("AlreadyCompleted" or "Deferred"))
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Terminal PR {PrId} processing failed; unrelated intake continues.", entry.PrId);
            }
        }
    }

    private static bool IsSha(string? value) =>
        value is { Length: 40 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal static async Task<JsonObject> RunTerminalAsync(
        PrPollTarget target,
        string prId,
        PrLifecycleState lifecycle,
        string artifactTipSha,
        string? landedSha,
        string? parentSha,
        CodeReviewDaemonOptions options,
        ReviewStore store,
        GitRunner git,
        IReadOnlyList<IPrProvider> providers,
        IReadOnlyList<IReviewCommentReader> readers,
        ReviewWorkflowRunner runner,
        CodeReviewDaemon.Sample.Workspace.Sandbox.HostGitPushAuthorization pushAuthorization,
        CancellationToken ct,
        ScopedCommandLineResult? command = null,
        PrOrchestrator? orchestrator = null,
        Func<
            ReviewRun,
            WorkflowRound,
            Func<CancellationToken, Task>,
            CancellationToken,
            Task<JsonObject?>
        >? finishClosure = null
    )
    {
        if (lifecycle is not (PrLifecycleState.Merged or PrLifecycleState.Abandoned))
            throw new InvalidDataException("Terminal admission requires a closed PR.");
        if (lifecycle == PrLifecycleState.Merged && (landedSha is null || parentSha is null))
            throw new InvalidDataException("Merged admission requires actual landed objects.");
        if (lifecycle == PrLifecycleState.Abandoned && (landedSha is not null || parentSha is not null))
            throw new InvalidDataException("Abandoned admission cannot claim landed objects.");
        var branch = ReviewBranchManager.BuildReviewBranchName(
            target.Repo,
            int.Parse(prId, CultureInfo.InvariantCulture)
        );
        var expectedTip = artifactTipSha;
        var mayBeClosed = false;
        // New admission requires the original remote branch; resume accepts only its own durable receipt.
        async Task VerifyBranchAsync()
        {
            var remote = await git.RunAsync(
                ["ls-remote", "--heads", "--", options.ResolvedStoreUrl!, "refs/heads/" + branch],
                AppContext.BaseDirectory,
                ct
            );
            if (
                !remote.Succeeded
                || !(
                    remote.Stdout.Trim() == expectedTip + "\trefs/heads/" + branch
                    || (mayBeClosed && string.IsNullOrWhiteSpace(remote.Stdout))
                )
            )
                throw new InvalidDataException("The exact merged artifact branch is absent or has changed.");
        }
        var provider = providers.Single(value => value.Provider == target.Provider);
        var descriptor =
            await provider.GetPullRequestAsync(target.Repo, prId, ct)
            ?? throw new InvalidDataException("Merged PR is unavailable.");
        if (
            descriptor.PrId != prId
            || descriptor.LifecycleState != lifecycle
            || (lifecycle == PrLifecycleState.Merged && descriptor.MergeCommitSha != landedSha)
            || string.IsNullOrWhiteSpace(descriptor.TargetRefName)
        )
            throw new InvalidDataException("Source PR no longer matches the exact merged request.");
        var repoId = store.EnsureRepo(target.Repo);
        var eventKey = command is not null
            ? $"merged:{artifactTipSha}:{landedSha}:{parentSha}"
            : $"terminal:{lifecycle}:{descriptor.HeadSha}:{descriptor.BaseSha}:{landedSha}:{descriptor.TargetRefName}";
        var seed = new ReviewRun
        {
            RepoId = repoId,
            PrId = descriptor.PrId,
            HeadSha = descriptor.HeadSha,
            BaseSha = descriptor.BaseSha,
            MergeSha = landedSha,
            TriggerWatermark = eventKey,
            ReviewKind = lifecycle == PrLifecycleState.Merged ? "merged-completion" : "abandoned-completion",
            VariantId = command is null ? "terminal" : artifactTipSha,
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = lifecycle,
            PrAuthor = descriptor.Author,
            PrTitle = descriptor.Title,
            PrDescription = descriptor.Description,
            ModelId = target.ModelId,
        };
        var existing = store.FindReviewRunByIdentity(seed);
        if (store.GetAmbiguousWorkflowRoundId(seed) is { } legacyRoundId)
            throw new InvalidOperationException($"Legacy workflow round {legacyRoundId} needs owner reconciliation.");
        if (command?.Fresh == true && existing is not null)
            throw new InvalidDataException("Fresh merged batch preparation cannot reuse an existing completion.");
        if (command?.ResumeMerged == true && existing is null)
            throw new InvalidDataException("Merged resume requires an existing exact completion identity.");
        if (existing is not null)
        {
            if (
                existing.MergeSha != seed.MergeSha
                || (
                    existing.TriggerWatermark != eventKey
                    && !(
                        command is null
                        && existing.TriggerWatermark.StartsWith(eventKey + ":fresh:", StringComparison.Ordinal)
                    )
                )
                || existing.Mode != "collect-only"
                || existing.PrLifecycleState != lifecycle
            )
                throw new InvalidDataException("Existing merged completion identity differs.");
            var existingRound = store.GetLatestWorkflowRound(existing.Id, WorkflowRoundKind.Merged);
            if (existingRound is not null && existingRound.EventKey == existing.TriggerWatermark)
            {
                var finishedClosure = store.TryGetLatestArtifact(existing.Id, "workflow-terminal-closure-finished");
                if (
                    finishedClosure is not null
                    && JsonNode.Parse(finishedClosure.Payload)?["WorkflowInstanceId"]?.GetValue<string>()
                        == existingRound.WorkflowInstanceId
                    && JsonNode.Parse(finishedClosure.Payload)?["Closed"]?.GetValue<bool>() == true
                )
                    return new JsonObject
                    {
                        ["RunId"] = existing.Id,
                        ["RoundId"] = existingRound.Id,
                        ["Outcome"] = "AlreadyCompleted",
                    };
                var key = WorkflowArtifactOperations.BuildRetentionKey(
                    target.Repo,
                    existing,
                    $"review-round-{existingRound.Id}"
                );
                var receipt = store.GetOutboxForRun(existing.Id).SingleOrDefault(value => value.IdempotencyKey == key);
                if (receipt is { Status: OutboxStatus.Posted, ProviderResponseId: { Length: 40 } retainedSha })
                {
                    expectedTip = retainedSha;
                    var integrated = store.TryGetLatestArtifact(existing.Id, "workflow-content-integration");
                    var proof = integrated is null ? null : JsonNode.Parse(integrated.Payload);
                    mayBeClosed =
                        proof?["RetainedSha"]?.GetValue<string>() == retainedSha
                        && proof["WorkflowInstanceId"]?.GetValue<string>() == $"review-round-{existingRound.Id}"
                        && proof["ArtifactBranch"]?.GetValue<string>() == branch;
                }
            }
        }
        if (
            command is null
            && existing is not null
            && store.GetLatestWorkflowRound(existing.Id, WorkflowRoundKind.Merged)
                is { Outcome: WorkflowRoundOutcome.Failed } failedRound
            && store.TryGetLatestArtifact(existing.Id, "workflow-attempt-reset") is { } resetArtifact
        )
        {
            var reset = JsonNode.Parse(resetArtifact.Payload)!;
            var assignment = JsonNode.Parse(
                store.TryGetLatestArtifact(existing.Id, Workspace.WorkflowWorkspace.AssignmentArtifactKind)!.Payload
            )!;
            if (
                assignment["Active"]?.GetValue<bool>() != false
                || reset["status"]?.GetValue<string>() != "reset"
                || reset["identity"]?["assignmentId"]?.GetValue<string>()
                    != assignment["AssignmentId"]?.GetValue<string>()
                || reset["identity"]?["workflowInstanceId"]?.GetValue<string>() != failedRound.WorkflowInstanceId
                || reset["identity"]?["runId"]?.GetValue<long>() != existing.Id
                || reset["identity"]?["head"]?.GetValue<string>() != seed.HeadSha
                || reset["identity"]?["base"]?.GetValue<string>() != seed.BaseSha
            )
                throw new InvalidDataException("Terminal retry lacks the selected attempt's accepted reset.");
            if (orchestrator is not null && !orchestrator.ShouldAttemptTerminal(existing.Id))
                return new JsonObject { ["Outcome"] = "Deferred" };
            if (
                store.TryGetLatestArtifact(existing.Id, "workflow-content-integration") is not null
                && finishClosure is not null
            )
            {
                await VerifyBranchAsync();
                using var closureCapability = ReviewArtifactBranchCapability.EnterScope();
                using var closurePush = pushAuthorization.EnterScope();
                ReviewArtifactBranchCapability.SetCurrent(
                    ReviewArtifactBranchCapability.Grant(target.Repo.NormalizedKey, existing.PrId, existing.HeadSha)
                );
                var terminalContext = JsonNode.Parse(failedRound.FrozenInputJson)!;
                async Task VerifyTerminalIdentityAsync(CancellationToken token)
                {
                    var current = await provider
                        .GetPullRequestAsync(target.Repo, existing.PrId, token)
                        .ConfigureAwait(false);
                    if (
                        current is null
                        || current.PrId != existing.PrId
                        || current.LifecycleState != lifecycle
                        || current.HeadSha != existing.HeadSha
                        || current.BaseSha != existing.BaseSha
                        || current.MergeCommitSha != existing.MergeSha
                        || current.TargetRefName != terminalContext["Merge"]?["TargetRefName"]?.GetValue<string>()
                    )
                        throw new InvalidDataException("Terminal source identity changed before closure deletion.");
                }
                JsonObject? closed;
                try
                {
                    closed = await finishClosure(existing, failedRound, VerifyTerminalIdentityAsync, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    orchestrator?.RecordTerminalFailure(existing.Id);
                    throw;
                }
                if (closed is null)
                    orchestrator?.RecordTerminalFailure(existing.Id);
                if (closed?["Closed"]?.GetValue<bool>() == true)
                {
                    store.AddArtifact(
                        new ReviewArtifact
                        {
                            ReviewRunId = existing.Id,
                            ArtifactKind = "workflow-terminal-closure-finished",
                            ArtifactSchemaVersion = 1,
                            Provider = "workflow",
                            Payload = new JsonObject
                            {
                                ["WorkflowInstanceId"] = failedRound.WorkflowInstanceId,
                                ["Closed"] = true,
                            }.ToJsonString(),
                        }
                    );
                    return new JsonObject
                    {
                        ["RunId"] = existing.Id,
                        ["RoundId"] = failedRound.Id,
                        ["Outcome"] = "ClosureFinished",
                    };
                }
            }
            if (
                existing.Generation >= options.MaxDurableRetryAttempts
                || (orchestrator is not null && !orchestrator.ShouldAttemptTerminal(existing.Id))
                || store.TryGetLatestArtifact(existing.Id, "workflow-content-integration") is not null
            )
                return new JsonObject { ["Outcome"] = "Deferred" };
            seed = seed with
            {
                Generation = checked(existing.Generation + 1),
                TriggerWatermark = $"{eventKey}:fresh:{existing.Id}",
            };
            eventKey = seed.TriggerWatermark;
            expectedTip = artifactTipSha;
            await VerifyBranchAsync();
            existing = store.InsertRerunReviewRun(seed);
        }
        else if (
            command is null
            && existing is not null
            && existing.TriggerWatermark.StartsWith(eventKey + ":fresh:", StringComparison.Ordinal)
        )
            eventKey = existing.TriggerWatermark;
        await VerifyBranchAsync();
        var run = existing ?? store.CreateOrGetReviewRun(seed);
        if (run.MergeSha != landedSha || run.TriggerWatermark != eventKey || run.ParkedAt is not null)
            throw new InvalidDataException("Existing completion identity requires reconciliation.");
        var round = store.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Merged);
        if (command?.ResumeMerged == true && round is null)
            throw new InvalidDataException("Merged resume requires an existing frozen round.");
        JsonObject frozen;
        if (round is not null)
        {
            if (round.EventKey != eventKey)
                throw new InvalidDataException("Another merged event already owns this source head.");
            if (round.CompletedAt is not null && round.Outcome == WorkflowRoundOutcome.Succeeded)
                return new JsonObject
                {
                    ["RunId"] = run.Id,
                    ["RoundId"] = round.Id,
                    ["Outcome"] = "AlreadyCompleted",
                };
            if (command is null && await runner.HasStartedRoundAsync(round, ct))
                return new JsonObject
                {
                    ["RunId"] = run.Id,
                    ["RoundId"] = round.Id,
                    ["Outcome"] = "Deferred",
                };
            frozen = JsonNode.Parse(round.FrozenInputJson)!.AsObject();
        }
        else
        {
            frozen = await PrCommentContextReader.ReadAsync(
                readers,
                store,
                target.Repo,
                target.Provider,
                repoId,
                run.PrId,
                descriptor,
                new Dictionary<string, string>(),
                ct
            );
            frozen["ContextManifestVersion"] = 1;
            frozen["CompletionEvidenceVersion"] = 1;
            frozen["Merge"] = new JsonObject
            {
                ["ArtifactBranch"] = branch,
                ["ArtifactTipSha"] = artifactTipSha,
                ["LandedSha"] = landedSha,
                ["ParentSha"] = parentSha,
                ["FinalSourceHeadSha"] = descriptor.HeadSha,
                ["ProviderBaseSha"] = descriptor.BaseSha,
                ["TargetRefName"] = descriptor.TargetRefName,
                ["DiscussionObservedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            var after = await provider.GetPullRequestAsync(target.Repo, run.PrId, ct);
            if (
                after is null
                || after.LifecycleState != lifecycle
                || (lifecycle == PrLifecycleState.Merged && after.MergeCommitSha != run.MergeSha)
                || after.HeadSha != run.HeadSha
                || after.BaseSha != run.BaseSha
                || after.TargetRefName != descriptor.TargetRefName
            )
                throw new InvalidDataException("Merged identity changed during discussion capture.");
            await VerifyBranchAsync();
            round = store.CreateOrGetWorkflowRound(
                new WorkflowRoundSeed
                {
                    ReviewRunId = run.Id,
                    RepoId = repoId,
                    PrId = run.PrId,
                    HeadSha = run.HeadSha,
                    Kind = WorkflowRoundKind.Merged,
                    EventKey = eventKey,
                    FrozenInputJson = frozen.ToJsonString(),
                }
            );
        }
        if (command?.ResumeMerged == true)
            await runner.ValidateMergedResumeAsync(run, round, frozen, command.MergedSlot, ct);
        if (command?.InspectMerged == true)
            return await runner.PrepareMergedOnlyAsync(run, round, frozen, ct, command.MergedSlot);
        if (command?.MergedSlot is { } selectedSlot)
        {
            var assignment = store.TryGetLatestArtifact(
                run.Id,
                CodeReviewDaemon.Sample.Workspace.WorkflowWorkspace.AssignmentArtifactKind
            );
            if (
                assignment is null
                || JsonNode.Parse(assignment.Payload)?["Slot"]?["Index"]?.GetValue<int>() != selectedSlot
            )
                throw new InvalidOperationException("Inspect the selected merged slot before execution.");
        }
        var current = await provider.GetPullRequestAsync(target.Repo, run.PrId, ct);
        if (
            current is null
            || current.LifecycleState != lifecycle
            || current.HeadSha != run.HeadSha
            || current.BaseSha != run.BaseSha
            || (lifecycle == PrLifecycleState.Merged && current.MergeCommitSha != run.MergeSha)
            || current.TargetRefName != descriptor.TargetRefName
        )
            throw new InvalidDataException("Merged source identity changed before execution.");
        await VerifyBranchAsync();
        using var capabilityScope = ReviewArtifactBranchCapability.EnterScope();
        using var pushScope = pushAuthorization.EnterScope();
        ReviewArtifactBranchCapability.SetCurrent(
            ReviewArtifactBranchCapability.Grant(target.Repo.NormalizedKey, run.PrId, run.HeadSha)
        );
        pushAuthorization.AuthorizePush(branch);
        var outcome = await runner.RunOrResumeAsync(run, round, frozen, ct);
        if (outcome == AchieveAi.LmDotnetTools.LmWorkflow.Runtime.WorkflowInvocationStatus.Failed)
            orchestrator?.RecordTerminalFailure(run.Id);
        return new JsonObject
        {
            ["RunId"] = run.Id,
            ["RoundId"] = round.Id,
            ["Outcome"] = outcome.ToString(),
        };
    }
}
