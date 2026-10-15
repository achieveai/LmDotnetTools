using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class ReviewWorkflowRunnerTests
{
    [Theory]
    [InlineData(
        "new_head",
        "prepare-independent,independent-review,independent-grade,collect-comments,fetch-discussion,assess-performance,collect-publication,retain-independent"
    )]
    [InlineData("discussion", "prepare-discussion,discussion,retain-discussion")]
    [InlineData(
        "merged",
        "prepare-history,merged-self-assessment,learnings,merged-judgment,collect-statistics,retain-merged,close-artifact-branch"
    )]
    public async Task Shipped_routes_complete_from_frozen_scope_before_releasing_workspace(
        string route,
        string expected
    )
    {
        using var fixture = new Fixture();
        var frozen = new JsonObject { ["window"] = route };
        var round = route == "new_head" ? null : fixture.CreateRound(route, frozen);
        var invoker = new RecordingInvoker();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker),
            (_, _) => new Dictionary<string, string> { ["review-parent"] = "parent-session" }
        );

        var status = await runner.RunAsync(fixture.Run, round, frozen, default);

        status.Should().Be(WorkflowInvocationStatus.Completed);
        invoker.Invoked.Select(Step).Should().Equal(expected.Split(','));
        if (route == "new_head")
            invoker.Invoked.Select(Step).Should().NotContain("publish", "collect-only reviews never post to the PR");
        fixture.Pool.Leased.Should().Be(1);
        fixture.Pool.Returned.Should().Be(1);
        fixture.Pool.Retired.Should().Be(0);
        fixture.Preparer.Calls.Should().Be(0, "the authored preparation script owns checkout preparation");

        var instanceId = round is null ? $"review-run-{fixture.Run.Id}" : $"review-round-{round.Id}";
        var snapshot = await fixture.WorkflowStore.LoadAsync(instanceId);
        snapshot.Should().NotBeNull();
        snapshot!.IsComplete.Should().BeTrue();
        snapshot.Inputs["Route"]!.GetValue<string>().Should().Be(route);
        snapshot.Sessions["review-parent"].Should().Be("parent-session");

        var scopeFiles = Directory.GetFiles(fixture.RunDirectoryRoot, "scope.json", SearchOption.AllDirectories);
        scopeFiles.Should().ContainSingle();
        Path.GetRelativePath(fixture.RunDirectoryRoot, scopeFiles[0])
            .Split(Path.DirectorySeparatorChar)
            .Should()
            .HaveCount(2, "the instance id is represented by one hashed directory");
        var scope = JsonNode.Parse(await File.ReadAllTextAsync(scopeFiles[0]))!.AsObject();
        scope["ReviewRunId"]!.GetValue<long>().Should().Be(fixture.Run.Id);
        scope["WorkflowInstanceId"]!.GetValue<string>().Should().Be(instanceId);
        scope["Admission"]!["Route"]!.GetValue<string>().Should().Be(route);
        JsonNode.DeepEquals(scope["FrozenContext"], frozen).Should().BeTrue();

        if (round is null)
        {
            var stored = fixture.Store.GetReviewRun(fixture.Run.Id)!;
            stored.Stage.Should().Be(ReviewStage.Posted);
            stored.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        }
        else
        {
            fixture.Store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Succeeded);
            fixture.Store.GetWorkflowRound(round.Id)!.CompletedAt.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Configured_invocation_timeout_is_applied_to_automatic_tasks()
    {
        using var fixture = new Fixture();
        var invoker = new RecordingInvoker();
        var startedAt = DateTimeOffset.UtcNow;
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker),
            invocationTimeout: TimeSpan.FromMinutes(7)
        );

        await runner.RunAsync(fixture.Run, null, [], default);

        invoker.Invoked.Should().NotBeEmpty();
        invoker.Invoked.Should().OnlyContain(value => value.DeadlineUtc.HasValue);
        invoker.Invoked[0].DeadlineUtc.Should().BeOnOrAfter(startedAt.AddMinutes(7));
        invoker.Invoked[0].DeadlineUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddMinutes(7));
    }

    [Fact]
    public async Task Later_round_reacquires_the_new_head_workspace_address()
    {
        using var fixture = new Fixture();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );
        await runner.RunAsync(fixture.Run, null, [], default);
        var frozen = new JsonObject { ["through"] = 12 };
        var round = fixture.CreateRound("discussion", frozen);

        await runner.RunAsync(fixture.Run, round, frozen, default);

        fixture.Pool.Leased.Should().Be(1);
        fixture.Pool.Preferred.Should().Be(1);
        fixture.Pool.Returned.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_snapshot_recovers_same_assignment_and_reconciles_without_replay()
    {
        using var fixture = new Fixture();
        var frozen = new JsonObject { ["through"] = 112 };
        var round = fixture.CreateRound("discussion", frozen);
        var first = new RecordingInvoker { UnknownTask = "discussion" };
        var firstRunner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(first));

        (await firstRunner.RunAsync(fixture.Run, round, frozen, default)).Should().Be(WorkflowInvocationStatus.Unknown);

        fixture.Pool.Leased.Should().Be(1);
        fixture.Pool.Returned.Should().Be(0);
        fixture.Workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        fixture.Store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Unknown);

        var resumedWorkspace = fixture.CreateWorkspace();
        var second = new RecordingInvoker();
        var resumedRunner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(second),
            workspace: resumedWorkspace
        );
        (await resumedRunner.RunAsync(fixture.Run, fixture.Store.GetWorkflowRound(round.Id), frozen, default))
            .Should()
            .Be(WorkflowInvocationStatus.Completed);

        fixture.Pool.Leased.Should().Be(1);
        fixture.Pool.Recovered.Should().Be(1);
        fixture.Pool.Returned.Should().Be(1);
        second.Reconciled.Should().ContainSingle();
        second
            .Reconciled[0]
            .InvocationId.Should()
            .Be(first.Invoked.Single(value => Step(value) == "discussion").InvocationId);
        second.Invoked.Select(Step).Should().Equal("retain-discussion");
    }

    [Fact]
    public async Task Resume_reads_the_original_frozen_scope_for_an_unknown_invocation()
    {
        using var fixture = new Fixture();
        var frozen = new JsonObject { ["messages"] = new JsonArray("c-1") };
        var first = new RecordingInvoker { UnknownTask = "independent-review" };
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(first));
        (await runner.RunAsync(fixture.Run, null, frozen, default)).Should().Be(WorkflowInvocationStatus.Unknown);

        var resumed = await runner.ResumeAsync(fixture.Run, null, default);

        resumed.Should().Be(WorkflowInvocationStatus.Completed);
        first.Reconciled.Should().ContainSingle();
    }

    [Fact]
    public async Task Expired_startup_reconciles_original_agent_and_discards_remaining_work_without_dispatch()
    {
        using var fixture = new Fixture();
        var first = new RecordingInvoker { UnknownTask = "independent-review" };
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(first));
        (await runner.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Unknown);
        var owner = $"review-run-{fixture.Run.Id}";
        var snapshot = (await fixture.WorkflowStore.LoadAsync(owner))!;
        var pending = snapshot.Tasks.Single(task => task.Status == WorkflowTaskStatus.InFlight);
        var deadlines = snapshot.Deadlines.ToDictionary(entry => entry.Key, entry => entry.Value);
        deadlines[pending.Name] = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.WorkflowStore.SaveAsync(owner, snapshot with { Deadlines = deadlines });
        var reconciler = new RecordingInvoker();
        var recovery = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(reconciler),
            workspace: fixture.CreateWorkspace(),
            resetFailedAttempts: true
        );

        await recovery.RecoverActiveWorkspacesAsync(default);

        reconciler.Reconciled.Should().ContainSingle();
        reconciler
            .Reconciled[0]
            .InvocationId.Should()
            .Be(first.Invoked.Single(value => Step(value) == "independent-review").InvocationId);
        reconciler.Invoked.Should().BeEmpty("startup must not send the never-dispatched remainder");
        var discarded = (await fixture.WorkflowStore.LoadAsync(owner))!;
        discarded.IsComplete.Should().BeFalse();
        discarded.Tasks.Should().Contain(task => task.Status == WorkflowTaskStatus.Failed);
        fixture.Store.GetReviewRun(fixture.Run.Id)!.WorkflowStatus.Should().NotBe(WorkflowStatus.Completed);
        fixture
            .Workspace.ReadAssignment(fixture.Run)
            .Active.Should()
            .BeTrue("missing cleanup proof must retain ownership");
        fixture
            .Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset-intent")
            .Should()
            .BeNull("inspection failed before destructive apply was authorized");
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset").Should().BeNull();
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-recovery-pending").Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_abandons_unknown_inflight_checkout_only_after_verified_reset(bool cleanupAlreadySettled)
    {
        using var fixture = new Fixture();
        var first = new RecordingInvoker { UnknownTask = "prepare-independent" };
        var initial = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(first));
        (await initial.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Unknown);
        var owner = $"review-run-{fixture.Run.Id}";
        var before = (await fixture.WorkflowStore.LoadAsync(owner))!;
        var task = before.Tasks.Single();
        task.Status.Should().Be(WorkflowTaskStatus.InFlight);
        var assignment = fixture.Workspace.ReadAssignment(fixture.Run);
        fixture.Store.AddArtifact(
            new()
            {
                ReviewRunId = fixture.Run.Id,
                ArtifactKind = WorkflowWorkspace.RemotePreparationArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = new JsonObject
                {
                    ["AssignmentId"] = assignment.AssignmentId,
                    ["Settled"] = false,
                    ["OutcomeUnknown"] = true,
                }.ToJsonString(),
            }
        );
        var unknown = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            WorkflowWorkspace.RemotePreparationArtifactKind
        )!;
        fixture.Store.AddArtifact(
            new()
            {
                ReviewRunId = fixture.Run.Id,
                ArtifactKind = WorkflowWorkspace.PreparationDiagnosticArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = new JsonObject
                {
                    ["AssignmentId"] = assignment.AssignmentId,
                    ["Phase"] = "checkout",
                }.ToJsonString(),
            }
        );
        var identity = new JsonObject
        {
            ["runId"] = fixture.Run.Id,
            ["assignmentId"] = assignment.AssignmentId,
            ["workflowInstanceId"] = owner,
            ["repository"] = "widgets",
            ["slot"] = 0,
            ["pr"] = fixture.Run.PrId,
            ["head"] = fixture.Run.HeadSha,
            ["base"] = fixture.Run.BaseSha,
            ["merge"] = new string('a', 40),
            ["sourcePath"] = assignment.Slot.SourceRelativePath,
        };
        fixture.CommandRunner.OnArgvContains(
            "baseline history is incomplete",
            new SandboxCommandResult(
                0,
                """{"repository":"widgets","slot":0,"sourcePath":".worktrees/widgets-0/repos/widgets","sourceStore":"source-stores/widgets.git","checkoutSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","mergeBaseSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","preparedManifest":"manifest"}""",
                ""
            )
        );
        foreach (var apply in new[] { false, true })
        {
            var receipt = new JsonObject
            {
                ["schema"] = 1,
                ["status"] = apply ? "reset" : "inspected",
                ["identity"] = identity.DeepClone(),
                ["compactOutput"] = true,
                ["inventoryCount"] = 2,
                ["inventorySha256"] = new string('a', 64),
            };
            fixture.CommandRunner.OnArgvContains(
                "\"apply\":" + apply.ToString().ToLowerInvariant(),
                new SandboxCommandResult(0, receipt.ToJsonString(), "")
            );
        }
        if (cleanupAlreadySettled)
            await fixture.Workspace.ResetSettledAttemptAsync(fixture.Run, owner, default);
        var invoker = new RecordingInvoker();
        var workspace = fixture.CreateWorkspace();
        var recovery = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker),
            workspace: workspace,
            resetFailedAttempts: true
        );
        await recovery.RecoverActiveWorkspacesAsync(default);
        var after = (await fixture.WorkflowStore.LoadAsync(owner))!;
        after.IsComplete.Should().BeFalse();
        var abandoned = after.Tasks.Single();
        abandoned.Status.Should().Be(WorkflowTaskStatus.Failed);
        abandoned.LastError.Should().Be("Preparation attempt abandoned after verified selected-slot reset.");
        abandoned.NodeId.Should().Be(task.NodeId);
        abandoned.TaskId.Should().Be(task.TaskId);
        abandoned.Name.Should().Be(task.Name);
        abandoned.Attempts.Should().Be(task.Attempts);
        abandoned.ToolCallId.Should().Be(task.ToolCallId);
        after.Deadlines.Should().BeEquivalentTo(before.Deadlines);
        invoker.Invoked.Should().BeEmpty();
        fixture.Pool.Returned.Should().Be(1);
        fixture
            .Store.GetArtifacts(fixture.Run.Id)
            .Single(artifact => artifact.Id == unknown.Id)
            .Payload.Should()
            .Be(unknown.Payload);
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, WorkflowWorkspace.CheckoutArtifactKind).Should().BeNull();
        fixture
            .Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-preparation-reset-disposition")
            .Should()
            .NotBeNull();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Startup_abandons_settled_context_failure_only_with_unchanged_evidence(
        bool cleanupAlreadySettled,
        bool evidenceChanged,
        bool terminal = false
    )
    {
        using var fixture = new Fixture();
        var run = fixture.Run;
        if (terminal)
        {
            fixture.Store.UpdateReviewRunState(run.Id, run.Stage, run.WorkflowStatus, PrLifecycleState.Merged);
            run = fixture.Store.GetReviewRun(run.Id)!;
        }
        var frozen = new JsonObject { ["original-evidence"] = "preserved" };
        var round = terminal ? fixture.CreateRound("merged", frozen) : null;
        var first = new RecordingInvoker { UnknownTask = terminal ? "prepare-history" : "prepare-independent" };
        var initial = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(first));
        (await initial.RunAsync(run, round, frozen, default)).Should().Be(WorkflowInvocationStatus.Unknown);
        var owner = terminal ? $"review-round-{round!.Id}" : $"review-run-{run.Id}";
        var before = (await fixture.WorkflowStore.LoadAsync(owner))!;
        var assignment = fixture.Workspace.ReadAssignment(fixture.Run);
        void Save(string kind, object payload) =>
            fixture.Store.AddArtifact(
                new()
                {
                    ReviewRunId = fixture.Run.Id,
                    ArtifactKind = kind,
                    ArtifactSchemaVersion = 1,
                    Provider = "workflow",
                    Payload = JsonSerializer.Serialize(payload),
                }
            );
        if (!terminal)
            Save(
                WorkflowWorkspace.CheckoutArtifactKind,
                new PreparedCheckout(
                    assignment.Slot.WorktreeRoot,
                    assignment.Slot.SourceRoot,
                    $"{assignment.Slot.WorktreeRoot}/PRs/{fixture.Run.PrId}",
                    "review/widgets-7",
                    CheckoutSha: new string('a', 40),
                    SourceHeadSha: fixture.Run.HeadSha,
                    TargetBaseSha: fixture.Run.BaseSha
                )
            );
        var preparation = new JsonObject
        {
            ["AssignmentId"] = assignment.AssignmentId,
            ["Settled"] = true,
            ["OutcomeUnknown"] = false,
        };
        Save(WorkflowWorkspace.RemotePreparationArtifactKind, preparation);
        var originalPreparation = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            WorkflowWorkspace.RemotePreparationArtifactKind
        )!;
        Save(
            WorkflowWorkspace.PreparationDiagnosticArtifactKind,
            new JsonObject
            {
                ["AssignmentId"] = assignment.AssignmentId,
                ["Phase"] = "context-read",
                ["WriteUnacknowledged"] = false,
                ["Errors"] = new JsonArray(new JsonObject { ["Type"] = nameof(IOException) }),
            }
        );
        var diagnostic = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            WorkflowWorkspace.PreparationDiagnosticArtifactKind
        )!;
        var identity = new JsonObject
        {
            ["runId"] = fixture.Run.Id,
            ["assignmentId"] = assignment.AssignmentId,
            ["workflowInstanceId"] = owner,
            ["repository"] = "widgets",
            ["slot"] = 0,
            ["pr"] = fixture.Run.PrId,
            ["head"] = fixture.Run.HeadSha,
            ["base"] = fixture.Run.BaseSha,
            ["merge"] = new string('a', 40),
            ["sourcePath"] = assignment.Slot.SourceRelativePath,
        };
        if (terminal)
        {
            identity["merge"] = null;
            identity["mode"] = "unprepared-baseline";
        }
        SandboxCommandResult Receipt(bool apply, bool bound)
        {
            var receiptIdentity = (JsonObject)identity.DeepClone();
            if (terminal && bound)
            {
                receiptIdentity["rootHead"] = new string('e', 40);
                receiptIdentity["sourceBaseline"] = new string('d', 40);
            }
            return new(
                0,
                new JsonObject
                {
                    ["schema"] = 1,
                    ["status"] = apply ? "reset" : "inspected",
                    ["identity"] = receiptIdentity,
                    ["rootHead"] = new string('e', 40),
                    ["sourceBaseline"] = new string('d', 40),
                    ["compactOutput"] = true,
                    ["inventoryCount"] = 2,
                    ["inventorySha256"] = new string('a', 64),
                }.ToJsonString(),
                ""
            );
        }
        fixture.CommandRunner.OnArgvContainsSequence("\"apply\":false", Receipt(false, false), Receipt(false, true));
        fixture.CommandRunner.OnArgvContains("\"apply\":true", Receipt(true, true));
        if (cleanupAlreadySettled)
            await fixture.Workspace.ResetSettledAttemptAsync(run, owner, default);
        if (evidenceChanged)
            fixture.CommandRunner.BeforeArgvContains(
                "\"apply\":true",
                () => Save(WorkflowWorkspace.RemotePreparationArtifactKind, preparation)
            );
        var invoker = new RecordingInvoker();
        var recovery = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker),
            workspace: fixture.CreateWorkspace(),
            resetFailedAttempts: true
        );
        await recovery.RecoverActiveWorkspacesAsync(default);
        var after = (await fixture.WorkflowStore.LoadAsync(owner))!;
        after.IsComplete.Should().BeFalse();
        after
            .Tasks.Single()
            .Status.Should()
            .Be(evidenceChanged ? WorkflowTaskStatus.InFlight : WorkflowTaskStatus.Failed);
        after.Tasks.Single().TaskId.Should().Be(before.Tasks.Single().TaskId);
        after.Tasks.Single().Attempts.Should().Be(before.Tasks.Single().Attempts);
        after.Tasks.Single().ToolCallId.Should().Be(before.Tasks.Single().ToolCallId);
        after.Deadlines.Should().BeEquivalentTo(before.Deadlines);
        invoker.Invoked.Should().BeEmpty();
        fixture.Pool.Returned.Should().Be(evidenceChanged ? 0 : 1);
        if (terminal)
        {
            fixture.Store.GetOutboxForRun(run.Id).Should().BeEmpty();
            fixture.Store.GetLatestWorkflowRound(run.Id, WorkflowRoundKind.Merged)!.CompletedAt.Should().BeNull();
            fixture.Store.TryGetLatestArtifact(run.Id, WorkflowWorkspace.CheckoutArtifactKind).Should().BeNull();
        }
        fixture
            .Store.GetArtifacts(fixture.Run.Id)
            .Single(artifact => artifact.Id == diagnostic.Id)
            .Payload.Should()
            .Be(diagnostic.Payload);
        fixture
            .Store.GetArtifacts(fixture.Run.Id)
            .Single(artifact => artifact.Id == originalPreparation.Id)
            .Payload.Should()
            .Be(originalPreparation.Payload);
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, WorkflowWorkspace.PreparationArtifactKind).Should().BeNull();
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset").Should().NotBeNull();
        var latestPreparation = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            WorkflowWorkspace.RemotePreparationArtifactKind
        )!;
        latestPreparation.Payload.Should().Be(originalPreparation.Payload);
        latestPreparation.Id.Should().BeGreaterOrEqualTo(originalPreparation.Id);
        if (evidenceChanged)
            latestPreparation.Id.Should().BeGreaterThan(originalPreparation.Id);
        var disposition = fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-preparation-reset-disposition");
        if (evidenceChanged)
        {
            disposition.Should().BeNull();
            fixture.Workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
            fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-recovery-pending").Should().NotBeNull();
        }
        else
            JsonNode.Parse(disposition!.Payload)!["Disposition"]!
                .GetValue<string>()
                .Should()
                .Be("Failed context preparation abandoned after verified cleanup.");
    }

    [Fact]
    public async Task Startup_recovery_restores_every_active_assignment_before_new_admission()
    {
        using var fixture = new Fixture();
        await fixture.Workspace.AcquireAsync(fixture.Run, $"review-run-{fixture.Run.Id}", default);
        var restartedWorkspace = fixture.CreateWorkspace();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker()),
            workspace: restartedWorkspace,
            resetFailedAttempts: true
        );

        await runner.RecoverActiveWorkspacesAsync(default);

        fixture.Pool.Recovered.Should().Be(1);
        restartedWorkspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        fixture
            .CommandRunner.Commands.Should()
            .NotContain(command => command.Argv.Any(arg => arg.Contains("compactOutput")));
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset-intent").Should().BeNull();
    }

    [Fact]
    public async Task Completed_snapshot_fast_path_releases_a_persisted_active_assignment()
    {
        using var fixture = new Fixture();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );
        await runner.RunAsync(fixture.Run, null, [], default);
        _ = await fixture.Workspace.AcquireAsync(fixture.Run, $"review-run-{fixture.Run.Id}", default);

        (await runner.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Completed);

        fixture.Pool.Returned.Should().Be(2);
        fixture
            .Workspace.Invoking(value => value.ReadAssignment(fixture.Run))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Integration")] // Real workflow persistence and SQLite fixture exercise the runner's completed fast path.
    public async Task Completed_snapshot_with_hosted_parent_busy_does_not_release_assignment_or_resend()
    {
        using var fixture = new Fixture();
        var recording = new RecordingInvoker();
        var setup = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(recording),
            (_, _) =>
                new Dictionary<string, string>
                {
                    ["review-parent"] = "parent-session",
                    ["review-grader"] = "grader-session",
                }
        );
        (await setup.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Completed);
        var instanceId = $"review-run-{fixture.Run.Id}";
        var snapshot = (await fixture.WorkflowStore.LoadAsync(instanceId))!;
        var validatedAgents = snapshot
            .Tasks.Where(occurrence =>
                occurrence.Status == WorkflowTaskStatus.Validated
                && snapshot
                    .Definition!.Nodes.OfType<ProceduralNode>()
                    .Single(node => node.Id == occurrence.NodeId)
                    .TaskList!.Single(task => task.Id == occurrence.TaskId)
                    .Delegate == DelegateKind.Agent
            )
            .ToArray();
        validatedAgents.Should().NotBeEmpty();
        var expiredDeadline = DateTimeOffset.UtcNow.AddMinutes(-1);
        snapshot = snapshot with
        {
            Deadlines = validatedAgents.ToDictionary(entry => entry.Name, _ => expiredDeadline, StringComparer.Ordinal),
        };
        await fixture.WorkflowStore.SaveAsync(instanceId, snapshot); // Saved deadlines govern reconciliation.
        var runDirectory = Path.GetDirectoryName(
            Directory.GetFiles(fixture.RunDirectoryRoot, "scope.json", SearchOption.AllDirectories).Single()
        )!;
        var privateDirectory = Path.Combine(runDirectory, "private");
        Directory.CreateDirectory(privateDirectory);
        foreach (var occurrence in validatedAgents)
        {
            var task = snapshot
                .Definition!.Nodes.OfType<ProceduralNode>()
                .Single(node => node.Id == occurrence.NodeId)
                .TaskList!.Single(value => value.Id == occurrence.TaskId);
            var sessionId = snapshot.Sessions[task.Session ?? occurrence.NodeId];
            var invocationId = occurrence.ToolCallId ?? $"{instanceId}/{occurrence.Name}/{occurrence.Attempts}";
            var invocation = new WorkflowInvocation
            {
                InstanceId = instanceId,
                InvocationId = invocationId,
                UnitName = occurrence.Name,
                Task = task,
                Input = new JsonObject(),
                SessionId = sessionId,
                Attempt = occurrence.Attempts + 1,
                DeadlineUtc = expiredDeadline,
            };
            await File.WriteAllTextAsync(
                Path.Combine(privateDirectory, "invocation-" + Hash(invocationId) + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        SchemaVersion = 1,
                        Invocation = invocation,
                        Result = new WorkflowInvocationResult(WorkflowInvocationStatus.Completed, "{\"Done\":true}"),
                    }
                )
            );
        }

        // The completed runner has already released its address. Give this saved workflow the same
        // pinned preparation/binding shape a real completed host run retained, then reacquire its slot.
        _ = await fixture.Workspace.AcquireAsync(fixture.Run, instanceId, default);
        var assignment = fixture.Workspace.ReadAssignment(fixture.Run);
        var prepared = new WorkflowWorkspacePreparation(
            assignment.Slot,
            null,
            new PreparedReviewWorkspace("nova-reviews", "workspace-1", fixture.Run.PrId),
            [],
            assignment.AssignmentId,
            []
        );
        fixture.Store.AddArtifact(
            new()
            {
                ReviewRunId = fixture.Run.Id,
                ArtifactKind = WorkflowWorkspace.PreparationArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = JsonSerializer.Serialize(prepared),
            }
        );
        foreach (
            var sessionId in validatedAgents
                .Select(occurrence =>
                {
                    var task = snapshot
                        .Definition!.Nodes.OfType<ProceduralNode>()
                        .Single(node => node.Id == occurrence.NodeId)
                        .TaskList!.Single(value => value.Id == occurrence.TaskId);
                    return snapshot.Sessions[task.Session ?? occurrence.NodeId];
                })
                .Distinct(StringComparer.Ordinal)
        )
        {
            fixture.Store.AddArtifact(
                new()
                {
                    ReviewRunId = fixture.Run.Id,
                    ArtifactKind = "workflow-session:" + Hash(sessionId),
                    ArtifactSchemaVersion = 1,
                    Provider = "provider",
                    Payload = JsonSerializer.Serialize(
                        new
                        {
                            SessionId = sessionId,
                            ThreadId = "thread-parent",
                            prepared.Workspace.WorkspaceId,
                            ModeId = "code-review-daemon",
                            PublicationPolicyVersion = 1,
                            prepared.Workspace.WorkingDirectoryRelPath,
                        }
                    ),
                }
            );
        }

        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/run-state", "{\"threadId\":\"thread-parent\",\"isInProgress\":true}")
            .OnJson(HttpMethod.Get, "/subagents?recursive=true", "{\"schemaVersion\":1,\"nodes\":[]}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://host/") };
        var client = new LmStreamingS2SClient(http, "secret", "app", "key");
        var options = new CodeReviewDaemonOptions { ReviewSubAgentBarrierQuietSeconds = 1 };
        var scopes = new WorkflowPublicationScopes(fixture.WorkflowStore, client);
        var runner = fixture.CreateRunner(
            (run, id, directory, _) =>
            {
                var dispatcher = new WorkflowOperationDispatcher(
                    fixture.Store,
                    fixture.Workspace,
                    options,
                    directory,
                    () => ReviewArtifactBranchCapability.Denied,
                    (_, _) => throw new InvalidOperationException("Completed fast path must not dispatch artifacts.")
                );
                IWorkflowTaskInvoker hosted = new ReviewWorkflowInvoker(
                    run,
                    id,
                    directory,
                    directory,
                    new WorkflowScriptInvoker(OperatingSystem.IsWindows() ? "python" : "python3"),
                    dispatcher,
                    client,
                    fixture.Workspace,
                    fixture.Store,
                    options,
                    scopes,
                    (_, _, _) => throw new InvalidOperationException("Completed fast path must not create scopes."),
                    new UnusedGatewaySkillProbe(),
                    NullLoggerFactory.Instance
                );
                return Task.FromResult(hosted);
            }
        );
        var releasedBefore = fixture.Pool.Returned;

        var status = await runner.RunAsync(fixture.Run, null, [], default);

        status.Should().Be(WorkflowInvocationStatus.Unknown);
        handler
            .CountRequests("/run-state")
            .Should()
            .BeGreaterThanOrEqualTo(2, "the real hosted invoker checks the busy parent around the roster");
        handler
            .CountRequests("subagents")
            .Should()
            .BeGreaterThanOrEqualTo(1, "the recursive roster must also be checked");
        handler.Requests.Should().OnlyContain(request => request.Method == HttpMethod.Get);
        fixture.Pool.Returned.Should().Be(releasedBefore, "a busy parent keeps its workflow assignment active");
        fixture.Workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        recording.Invoked.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Startup_recovery_releases_active_assignment_when_every_durable_workflow_is_settled()
    {
        using var fixture = new Fixture();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );
        await runner.RunAsync(fixture.Run, null, [], default);
        _ = await fixture.Workspace.AcquireAsync(fixture.Run, $"review-run-{fixture.Run.Id}", default);
        var restartedWorkspace = fixture.CreateWorkspace();
        var restartedRunner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker()),
            workspace: restartedWorkspace
        );

        await restartedRunner.RecoverActiveWorkspacesAsync(default);

        fixture.Pool.Recovered.Should().Be(1);
        fixture.Pool.Returned.Should().Be(2);
        restartedWorkspace
            .Invoking(value => value.ReadAssignment(fixture.Run))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Existing_scope_with_different_frozen_context_is_rejected_before_factory_or_new_lease()
    {
        using var fixture = new Fixture();
        var factoryCalls = 0;
        var runner = fixture.CreateRunner(
            (_, _, _, _) =>
            {
                factoryCalls++;
                return Task.FromResult<IWorkflowTaskInvoker>(
                    new RecordingInvoker { UnknownTask = "prepare-independent" }
                );
            }
        );
        var first = new JsonObject { ["messages"] = new JsonArray(1) };
        (await runner.RunAsync(fixture.Run, null, first, default)).Should().Be(WorkflowInvocationStatus.Unknown);

        var changed = new JsonObject { ["messages"] = new JsonArray(1, 2) };
        await runner
            .Invoking(value => value.RunAsync(fixture.Run, null, changed, default))
            .Should()
            .ThrowAsync<InvalidDataException>()
            .WithMessage("*scope*");

        factoryCalls.Should().Be(1);
        fixture.Pool.Leased.Should().Be(1);
        fixture.Pool.Returned.Should().Be(0);
    }

    [Fact]
    public async Task Factory_failure_releases_unused_workspace_assignment()
    {
        using var fixture = new Fixture();
        var runner = fixture.CreateRunner((_, _, _, _) => throw new InvalidOperationException("factory failed"));

        await runner
            .Invoking(value => value.RunAsync(fixture.Run, null, [], default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("factory failed");

        fixture.Pool.Returned.Should().Be(1);
        fixture.Pool.Retired.Should().Be(0);
        fixture.InvokerFactoryCalls.Should().Be(1);
    }

    [Fact]
    public async Task Unavailable_workspace_defers_before_factory_or_external_invocation()
    {
        using var fixture = new Fixture();
        fixture.Pool.Available = false;
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );

        await runner
            .Invoking(value => value.RunAsync(fixture.Run, null, [], default))
            .Should()
            .ThrowAsync<WorkspaceCapacityUnavailableException>();

        fixture.InvokerFactoryCalls.Should().Be(0);
        fixture.Pool.Returned.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_run_holds_the_only_slot_but_does_not_block_its_own_next_reconciliation_pass()
    {
        using var fixture = new Fixture();
        var firstInvoker = new RecordingInvoker { UnknownTask = "independent-review" };
        var firstRunner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(firstInvoker));
        (await firstRunner.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Unknown);
        var waitingRun = fixture.CreateRun("8", "other-head");

        await firstRunner
            .Invoking(value => value.RunAsync(waitingRun, null, [], default))
            .Should()
            .ThrowAsync<WorkspaceCapacityUnavailableException>();

        (await firstRunner.ResumeAsync(fixture.Run, null, default)).Should().Be(WorkflowInvocationStatus.Completed);
        firstInvoker.Reconciled.Should().ContainSingle();
        fixture.Pool.Returned.Should().Be(1);
    }

    [Fact]
    public async Task Unknown_primary_blocks_a_discussion_until_the_primary_owner_reconciles()
    {
        using var fixture = new Fixture();
        var primary = new RecordingInvoker { UnknownTask = "independent-review" };
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(primary));
        (await runner.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Unknown);
        var round = fixture.CreateRound("discussion", new JsonObject { ["through"] = 12 });
        var factoryCalls = fixture.InvokerFactoryCalls;

        await runner
            .Invoking(value => value.RunAsync(fixture.Run, round, new JsonObject { ["through"] = 12 }, default))
            .Should()
            .ThrowAsync<WorkspaceCapacityUnavailableException>();

        fixture.InvokerFactoryCalls.Should().Be(factoryCalls);
        fixture.Workspace.ReadAssignment(fixture.Run).WorkflowInstanceId.Should().Be($"review-run-{fixture.Run.Id}");
        fixture.Pool.Returned.Should().Be(0);
        (await runner.ResumeAsync(fixture.Run, null, default)).Should().Be(WorkflowInvocationStatus.Completed);
        primary.Reconciled.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_discussion_blocks_merge_and_completed_primary_fast_path_cannot_release_it()
    {
        using var fixture = new Fixture();
        var primary = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );
        await primary.RunAsync(fixture.Run, null, [], default);
        var discussionContext = new JsonObject { ["through"] = 12 };
        var discussion = fixture.CreateRound("discussion", discussionContext);
        var discussionInvoker = new RecordingInvoker
        {
            UnknownTask = "discussion",
            UnknownReconcileTask = "discussion",
        };
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(discussionInvoker));
        (await runner.RunAsync(fixture.Run, discussion, discussionContext, default))
            .Should()
            .Be(WorkflowInvocationStatus.Unknown);
        var returnedBeforeFastPath = fixture.Pool.Returned;

        (await primary.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Completed);
        fixture.Pool.Returned.Should().Be(returnedBeforeFastPath);
        fixture.Workspace.ReadAssignment(fixture.Run).WorkflowInstanceId.Should().Be($"review-round-{discussion.Id}");

        var mergedContext = new JsonObject { ["merged"] = true };
        var merged = fixture.CreateRound("merged", mergedContext);
        var factoryCalls = fixture.InvokerFactoryCalls;
        var orchestrator = new PrOrchestrator(fixture.Store, runner, NullLogger<PrOrchestrator>.Instance);

        (await orchestrator.RunRoundAsync(fixture.Run, merged, default)).Should().Be(WorkflowInvocationStatus.Unknown);
        fixture.InvokerFactoryCalls.Should().Be(factoryCalls + 1, "only the active discussion owner is reconciled");
        fixture.Pool.Returned.Should().Be(returnedBeforeFastPath);
        discussionInvoker.Invoked.Should().NotContain(value => Step(value) == "prepare-history");
        fixture.Workspace.ReadAssignment(fixture.Run).WorkflowInstanceId.Should().Be($"review-round-{discussion.Id}");

        discussionInvoker.UnknownReconcileTask = null;
        (await orchestrator.RunRoundAsync(fixture.Run, merged, default))
            .Should()
            .Be(WorkflowInvocationStatus.Completed);
        discussionInvoker.Reconciled.Should().HaveCount(2);
        discussionInvoker.Invoked.Should().Contain(value => Step(value) == "prepare-history");
        fixture.Pool.Returned.Should().Be(returnedBeforeFastPath + 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_new_head_resets_before_release_and_bad_receipt_remains_owned_after_recovery(
        bool invalidReceipt
    )
    {
        using var fixture = new Fixture();
        var calls = 0;
        var invoker = new RecordingInvoker { FailedTask = "independent-review" };
        var owner = $"review-run-{fixture.Run.Id}";
        var runner = fixture.CreateRunner(
            (run, instance, _, _) =>
            {
                instance.Should().Be(owner);
                var assignment = fixture.Workspace.ReadAssignment(run);
                fixture.Store.AddArtifact(
                    new()
                    {
                        ReviewRunId = run.Id,
                        ArtifactKind = WorkflowWorkspace.PreparationArtifactKind,
                        ArtifactSchemaVersion = 1,
                        Provider = "workflow",
                        Payload = JsonSerializer.Serialize(
                            new WorkflowWorkspacePreparation(
                                assignment.Slot,
                                new PreparedCheckout(
                                    assignment.Slot.WorktreeRoot,
                                    assignment.Slot.SourceRoot,
                                    assignment.Slot.WorktreeRoot + "/PRs/7",
                                    "review/widgets-7",
                                    CheckoutSha: "merge"
                                ),
                                new PreparedReviewWorkspace("nova-reviews", "workspace-1", run.PrId),
                                [],
                                assignment.AssignmentId,
                                []
                            )
                        ),
                    }
                );
                var identity = new JsonObject
                {
                    ["runId"] = run.Id,
                    ["assignmentId"] = assignment.AssignmentId,
                    ["workflowInstanceId"] = owner,
                    ["repository"] = "widgets",
                    ["slot"] = 0,
                    ["pr"] = run.PrId,
                    ["head"] = run.HeadSha,
                    ["base"] = run.BaseSha,
                    ["merge"] = "merge",
                    ["sourcePath"] = assignment.Slot.SourceRelativePath,
                };
                JsonObject Receipt(bool apply)
                {
                    var receipt = new JsonObject
                    {
                        ["schema"] = 1,
                        ["status"] = apply ? "reset" : "inspected",
                        ["identity"] = identity.DeepClone(),
                        ["compactOutput"] = true,
                        ["inventoryCount"] = 2,
                        ["inventorySha256"] = new string('a', 64),
                    };
                    if (apply && invalidReceipt)
                        receipt["identity"]!["slot"] = 1;
                    return receipt;
                }
                fixture.CommandRunner.OnArgvContainsSequence(
                    "\"apply\":false",
                    new SandboxCommandResult(0, Receipt(false).ToJsonString(), "")
                );
                fixture.CommandRunner.OnArgvContainsSequence(
                    "\"apply\":true",
                    new SandboxCommandResult(0, Receipt(true).ToJsonString(), "")
                );
                fixture.CommandRunner.BeforeArgvContains(
                    "compactOutput",
                    () =>
                    {
                        calls++;
                        fixture.Pool.Returned.Should().Be(0);
                        fixture.Workspace.ReadAssignment(run).Should().Be(assignment);
                    }
                );
                return Task.FromResult<IWorkflowTaskInvoker>(invoker);
            },
            resetFailedAttempts: true
        );
        if (invalidReceipt)
            await runner
                .Invoking(value => value.RunAsync(fixture.Run, null, [], default))
                .Should()
                .ThrowAsync<InvalidDataException>();
        else
            (await runner.RunAsync(fixture.Run, null, [], default)).Should().Be(WorkflowInvocationStatus.Failed);
        calls.Should().Be(2);
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset-intent").Should().NotBeNull();
        var snapshot = (await fixture.WorkflowStore.LoadAsync(owner))!;
        snapshot.IsComplete.Should().BeFalse();
        snapshot
            .Tasks.Should()
            .Contain(task => task.NodeId == "independent-review" && task.Status == WorkflowTaskStatus.Failed);
        if (invalidReceipt)
        {
            fixture.Pool.Returned.Should().Be(0);
            fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset").Should().BeNull();
            fixture.Workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
            var restartedWorkspace = fixture.CreateWorkspace();
            var restartedRunner = fixture.CreateRunner(
                (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker),
                workspace: restartedWorkspace,
                resetFailedAttempts: true
            );
            await restartedRunner.RecoverActiveWorkspacesAsync(default);
            fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-recovery-pending").Should().NotBeNull();
            fixture.Pool.Recovered.Should().Be(1);
            fixture.Pool.Returned.Should().Be(0);
            restartedWorkspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        }
        else
        {
            fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset").Should().NotBeNull();
            fixture.Pool.Returned.Should().Be(1);
        }
    }

    [Theory]
    [InlineData("Open", false, false)]
    [InlineData("Merged", false, false)]
    [InlineData("Abandoned", false, false)]
    [InlineData("Abandoned", true, false)]
    [InlineData("Abandoned", false, true)]
    public async Task Settled_root_and_terminal_cleanup_binds_baselines_before_return_and_preserves_failure(
        string lifecycleName,
        bool failed,
        bool invalidReceipt
    )
    {
        var lifecycle = Enum.Parse<PrLifecycleState>(lifecycleName);
        using var fixture = new Fixture();
        fixture.Store.UpdateReviewRunState(fixture.Run.Id, fixture.Run.Stage, fixture.Run.WorkflowStatus, lifecycle);
        var run = fixture.Store.GetReviewRun(fixture.Run.Id)!;
        var frozen = new JsonObject { ["original-evidence"] = "preserved" };
        var round = lifecycle == PrLifecycleState.Open ? null : fixture.CreateRound("merged", frozen);
        var owner = round is null ? $"review-run-{run.Id}" : $"review-round-{round.Id}";
        var invoker = new RecordingInvoker
        {
            FailedTask = failed ? "merged-self-assessment" : null,
            TerminalState = lifecycle.ToString(),
        };
        var requests = new List<JsonObject>();
        var runner = fixture.CreateRunner(
            (current, instance, _, _) =>
            {
                instance.Should().Be(owner);
                var assignment = fixture.Workspace.ReadAssignment(current);
                var identity = new JsonObject
                {
                    ["runId"] = run.Id,
                    ["assignmentId"] = assignment.AssignmentId,
                    ["workflowInstanceId"] = owner,
                    ["repository"] = assignment.Slot.RepositoryName,
                    ["slot"] = assignment.Slot.Index,
                    ["pr"] = run.PrId,
                    ["head"] = run.HeadSha,
                    ["base"] = run.BaseSha,
                    ["merge"] = null,
                    ["sourcePath"] = assignment.Slot.SourceRelativePath,
                    ["mode"] = "unprepared-baseline",
                };
                var bound = identity.DeepClone().AsObject();
                bound["rootHead"] = new string('e', 40);
                bound["sourceBaseline"] = new string('d', 40);
                JsonObject Receipt(bool apply) =>
                    new()
                    {
                        ["schema"] = 1,
                        ["cleanupVersion"] = 2,
                        ["status"] = apply ? "reset" : "inspected",
                        ["identity"] = (apply ? bound : identity).DeepClone(),
                        ["rootHead"] = new string('e', 40),
                        ["sourceBaseline"] = new string('d', 40),
                        ["compactOutput"] = true,
                        ["inventoryCount"] = 33,
                        ["inventorySha256"] = new string('a', 64),
                    };
                var applied = Receipt(true);
                if (invalidReceipt)
                    applied["identity"]!["assignmentId"] = new string('f', 32);
                fixture.CommandRunner.OnArgvContainsSequence(
                    "\"apply\":false",
                    new SandboxCommandResult(0, Receipt(false).ToJsonString(), "")
                );
                fixture.CommandRunner.OnArgvContainsSequence(
                    "\"apply\":true",
                    new SandboxCommandResult(0, applied.ToJsonString(), "")
                );
                fixture.CommandRunner.BeforeArgvContains(
                    "compactOutput",
                    () =>
                    {
                        fixture.Pool.Returned.Should().Be(0);
                        fixture.Workspace.ReadAssignment(current).Should().Be(assignment);
                        var request = JsonNode.Parse(fixture.CommandRunner.Commands.Last().Argv.Last())!.AsObject();
                        var apply = request["apply"]!.GetValue<bool>();
                        request.Remove("apply");
                        request.Remove("compactOutput");
                        JsonNode.DeepEquals(request, apply ? bound : identity).Should().BeTrue();
                        if (apply)
                        {
                            var intent = fixture.Store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset-intent")!;
                            JsonNode.DeepEquals(JsonNode.Parse(intent.Payload), bound).Should().BeTrue();
                        }
                        requests.Add(request);
                    }
                );
                return Task.FromResult<IWorkflowTaskInvoker>(invoker);
            },
            resetFailedAttempts: true
        );

        if (invalidReceipt)
            await runner
                .Invoking(value => value.RunAsync(run, round, frozen, default))
                .Should()
                .ThrowAsync<InvalidDataException>();
        else
            (await runner.RunAsync(run, round, frozen, default))
                .Should()
                .Be(failed ? WorkflowInvocationStatus.Failed : WorkflowInvocationStatus.Completed);

        requests.Should().HaveCount(2);
        fixture.Pool.Retired.Should().Be(0);
        fixture.Pool.Returned.Should().Be(invalidReceipt ? 0 : 1);
        fixture.Store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset-intent").Should().NotBeNull();
        if (invalidReceipt)
        {
            fixture.Workspace.ReadAssignment(run).Active.Should().BeTrue();
            fixture.Store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset").Should().BeNull();
        }
        else
        {
            fixture.Store.TryGetLatestArtifact(run.Id, "workflow-attempt-reset").Should().NotBeNull();
            fixture.Workspace.Invoking(value => value.ReadAssignment(run)).Should().Throw<InvalidOperationException>();
        }
        var snapshot = (await fixture.WorkflowStore.LoadAsync(owner))!;
        snapshot.IsComplete.Should().Be(!failed);
        if (failed)
            snapshot
                .Tasks.Should()
                .Contain(task =>
                    task.NodeId == "merged-self-assessment"
                    && task.Status == WorkflowTaskStatus.Failed
                    && task.LastError == "invocation failed; automatic action replay is disabled"
                );
        if (round is not null)
        {
            var stored = fixture.Store.GetWorkflowRound(round.Id)!;
            stored.FrozenInputJson.Should().Be(round.FrozenInputJson);
            stored
                .Outcome.Should()
                .Be(
                    invalidReceipt ? WorkflowRoundOutcome.Running
                    : failed ? WorkflowRoundOutcome.Failed
                    : WorkflowRoundOutcome.Succeeded
                );
        }
    }

    [Fact]
    public async Task Failed_preparation_result_stays_unfinished_but_releases_durably_settled_workspace()
    {
        using var fixture = new Fixture();
        var frozen = new JsonObject { ["through"] = 112 };
        var round = fixture.CreateRound("discussion", frozen);
        var invoker = new RecordingInvoker { FailedTask = "prepare-discussion" };
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker));

        (await runner.RunAsync(fixture.Run, round, frozen, default)).Should().Be(WorkflowInvocationStatus.Failed);

        fixture.Store.GetWorkflowRound(round.Id)!.Outcome.Should().Be(WorkflowRoundOutcome.Failed);
        fixture.Store.GetWorkflowRound(round.Id)!.CompletedAt.Should().BeNull();
        fixture.Pool.Returned.Should().Be(1);
        fixture.Pool.Retired.Should().Be(0);
    }

    [Fact]
    public async Task Save_failure_after_invocation_keeps_active_assignment_for_reconciliation()
    {
        using var fixture = new Fixture(workflowStore: new FailingWorkflowStore());
        var invoker = new RecordingInvoker();
        var runner = fixture.CreateRunner((_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(invoker));

        await runner
            .Invoking(value => value.RunAsync(fixture.Run, null, [], default))
            .Should()
            .ThrowAsync<IOException>();

        invoker.Invoked.Should().ContainSingle();
        fixture.Pool.Returned.Should().Be(0);
        fixture.Pool.Retired.Should().Be(0);
        fixture.Workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        var saved = await fixture.WorkflowStore.LoadAsync($"review-run-{fixture.Run.Id}");
        saved!.Tasks.Should().ContainSingle(value => value.Status == WorkflowTaskStatus.InFlight);
    }

    [Fact]
    public async Task Shipped_statistics_wrapper_invokes_the_real_daemon_host_and_store()
    {
        using var fixture = new Fixture();
        var runner = fixture.CreateRunner(
            (_, _, _, _) => Task.FromResult<IWorkflowTaskInvoker>(new RecordingInvoker())
        );
        await runner.RunAsync(fixture.Run, null, [], default);
        var scopePath = Directory
            .GetFiles(fixture.RunDirectoryRoot, "scope.json", SearchOption.AllDirectories)
            .Single();
        var scope = JsonNode.Parse(await File.ReadAllTextAsync(scopePath))!;
        var scripts = new WorkflowScriptInvoker(
            pythonExecutable: OperatingSystem.IsWindows() ? "python" : "python3",
            environment: new Dictionary<string, string>
            {
                ["REVIEW_DAEMON_EXECUTABLE"] = typeof(ReviewWorkflowRunner).Assembly.Location,
                ["CodeReviewDaemon__DatabasePath"] = fixture.DatabasePath,
                ["CodeReviewDaemon__WorkflowStateDirectory"] = Path.GetDirectoryName(fixture.RunDirectoryRoot)!,
                ["CodeReviewDaemon__ReviewBotRepoUrl"] = "https://example.invalid/owner/store.git",
                ["CodeReviewDaemon__WorkspaceHostRoot"] = Path.Combine(fixture.RunDirectoryRoot, "host"),
                ["CodeReviewDaemon__EnableCommentPosting"] = "false",
                ["CodeReviewDaemon__UseS2SReviewAgent"] = "true",
                ["CodeReviewDaemon__LmStreamingBaseUrl"] = "http://127.0.0.1:1",
                ["SandboxGateway__WorkspaceBasePath"] = Path.Combine(fixture.RunDirectoryRoot, "gateway"),
            }
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var output = await scripts.InvokeAsync(
            "scripts/collect-statistics.py",
            Path.GetDirectoryName(WorkflowPath())!,
            new JsonObject
            {
                ["RunId"] = fixture.Run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["StepId"] = "collect-statistics",
                ["Attempt"] = 1,
                ["RunDirectory"] = Path.GetDirectoryName(scopePath)!,
            },
            scope["Admission"]!.DeepClone(),
            timeout.Token
        );
        var statistics = JsonNode.Parse(output)!;
        statistics["RunId"]!
            .GetValue<string>()
            .Should()
            .Be(fixture.Run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        statistics["ArtifactCount"]!.GetValue<int>().Should().Be(fixture.Store.GetArtifacts(fixture.Run.Id).Count);
        statistics["ReceiptCount"]!.GetValue<int>().Should().Be(fixture.Store.GetOutboxForRun(fixture.Run.Id).Count);
    }

    [Fact]
    public async Task Containment_uncertainty_keeps_only_its_owner_while_an_independent_run_completes()
    {
        using var fixture = new Fixture();
        var pool = new ReviewSlotPool(["widgets"], 2, NullLogger<ReviewSlotPool>.Instance);
        var workspace = fixture.CreateWorkspace(pool);
        var runner = fixture.CreateRunner(
            (run, _, _, _) =>
                Task.FromResult<IWorkflowTaskInvoker>(
                    new RecordingInvoker { ContainmentFailure = run.Id == fixture.Run.Id }
                ),
            workspace: workspace
        );
        await runner
            .Invoking(value => value.RunAsync(fixture.Run, null, [], default))
            .Should()
            .ThrowAsync<WorkflowScriptTerminationException>();
        workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        fixture.Store.GetReviewRun(fixture.Run.Id)!.ParkedAt.Should().NotBeNull();
        var restartedRunner = fixture.CreateRunner(
            (_, _, _, _) => throw new InvalidOperationException("must not reconcile quarantined work"),
            workspace: workspace
        );
        await restartedRunner
            .Invoking(value => value.ResumeAsync(fixture.Run, null, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Parked review runs*");
        var instanceId = $"review-run-{fixture.Run.Id}";
        var saved = (await fixture.WorkflowStore.LoadAsync(instanceId))!;
        await fixture.WorkflowStore.SaveAsync(instanceId, saved with { IsComplete = true, Tasks = [] });
        var restartPool = new ReviewSlotPool(["widgets"], 2, NullLogger<ReviewSlotPool>.Instance);
        var recovery = fixture.CreateRunner(
            (_, _, _, _) => throw new InvalidOperationException("recovery must not dispatch"),
            workspace: fixture.CreateWorkspace(restartPool),
            resetFailedAttempts: true
        );
        await recovery.RecoverActiveWorkspacesAsync(default);
        fixture
            .CommandRunner.Commands.Should()
            .NotContain(command => command.Argv.Any(arg => arg.Contains("compactOutput")));
        fixture.Store.TryGetLatestArtifact(fixture.Run.Id, "workflow-attempt-reset-intent").Should().BeNull();
        workspace
            .ReadAssignment(fixture.Run)
            .Active.Should()
            .BeTrue("a completion receipt cannot clear durable containment quarantine");
        var independent = fixture.CreateRun("another-pr", "another-head");
        (await runner.RunAsync(independent, null, [], default)).Should().Be(WorkflowInvocationStatus.Completed);
        workspace.ReadAssignment(fixture.Run).Active.Should().BeTrue();
        fixture.Store.ListActiveWorkflowWorkspaceRuns().Select(run => run.Id).Should().Equal(fixture.Run.Id);
        var available = await pool.TryLeaseAsync("widgets", default);
        available.Should().NotBeNull();
        (await pool.TryLeaseAsync("widgets", default))
            .Should()
            .BeNull("only the quarantined owner's capacity remains occupied");
        await pool.ReturnAsync(available!, default);
    }

    [Fact]
    public async Task Admitted_package_pins_assets_and_refuses_corrupt_or_unpinned_resume()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.RunDirectoryRoot, "source");
        Directory.CreateDirectory(source);
        var originalRoot = Path.GetDirectoryName(WorkflowPath())!;
        foreach (var file in Directory.EnumerateFiles(originalRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(source, Path.GetRelativePath(originalRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var liveWorkflow = Path.Combine(source, "workflow.yaml");
        var runRoot = Path.Combine(fixture.RunDirectoryRoot, "admitted");
        var admitted = await WorkflowPackageSnapshot.PrepareAsync(liveWorkflow, runRoot, false, default);
        var originalYaml = await File.ReadAllTextAsync(admitted);
        var skill = Directory.GetFiles(Path.Combine(source, "skills"), "*", SearchOption.AllDirectories)[0];
        var script = Directory.GetFiles(Path.Combine(source, "scripts"), "*.py")[0];
        await File.WriteAllTextAsync(skill, "mutated skill");
        await File.WriteAllTextAsync(script, "mutated script");
        await File.WriteAllTextAsync(liveWorkflow, "mutated workflow");
        (await WorkflowPackageSnapshot.PrepareAsync(liveWorkflow, runRoot, true, default)).Should().Be(admitted);
        (await File.ReadAllTextAsync(admitted)).Should().Be(originalYaml);
        (await File.ReadAllTextAsync(Path.Combine(runRoot, "package", Path.GetRelativePath(source, script))))
            .Should()
            .NotBe("mutated script");
        await File.WriteAllTextAsync(admitted, "corrupted admitted bytes");
        Func<Task> corrupt = () => WorkflowPackageSnapshot.PrepareAsync(liveWorkflow, runRoot, true, default);
        await corrupt.Should().ThrowAsync<InvalidDataException>();
        Func<Task> legacy = () =>
            WorkflowPackageSnapshot.PrepareAsync(
                liveWorkflow,
                Path.Combine(fixture.RunDirectoryRoot, "legacy"),
                true,
                default
            );
        await legacy.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Coordinator_lease_excludes_a_second_owner_and_can_be_reacquired_after_release()
    {
        using var fixture = new Fixture();
        var database = Path.Combine(fixture.RunDirectoryRoot, "lease.db");
        using (CodeReviewDaemon.Sample.Hosting.WorkflowCoordinatorLease.Acquire(database))
        {
            Action second = () => CodeReviewDaemon.Sample.Hosting.WorkflowCoordinatorLease.Acquire(database).Dispose();
            second.Should().Throw<InvalidOperationException>().WithMessage("Another workflow coordinator*");
            (await ProbeOtherProcess())
                .Should()
                .Be(23, "the second process must not acquire a live coordinator's lock");
        }
        (await ProbeOtherProcess()).Should().Be(0, "process exit or disposal releases ownership for a replacement");
        using var replacement = CodeReviewDaemon.Sample.Hosting.WorkflowCoordinatorLease.Acquire(database);
        replacement.CanWrite.Should().BeTrue();

        async Task<int> ProbeOtherProcess()
        {
            var start = new System.Diagnostics.ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                "try { $lease = [System.IO.FileStream]::new($env:WORKFLOW_TEST_LEASE_PATH, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); $lease.Dispose(); exit 0 } catch { exit 23 }"
            );
            start.Environment["WORKFLOW_TEST_LEASE_PATH"] = Path.GetFullPath(database) + ".coordinator.lock";
            using var child = System.Diagnostics.Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                child.Kill(entireProcessTree: true);
                throw;
            }
            return child.ExitCode;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class UnusedGatewaySkillProbe : IGatewaySkillProbe
    {
        public Task<GatewaySkillSupport> ProbeAsync(IReadOnlyList<string> marketplaces, CancellationToken ct) =>
            throw new InvalidOperationException("Completed fast path must not probe the gateway.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "review-workflow-runner-tests",
            Guid.NewGuid().ToString("N")
        );

        public Fixture(IWorkflowStore? workflowStore = null)
        {
            Store = new ReviewStore(_database.ConnectionString);
            var repoId = Store.EnsureRepo(
                new RepoIdentity
                {
                    Provider = "github",
                    OrgOrOwner = "example",
                    RepoName = "widgets",
                    RepoStableId = "repo-1",
                }
            );
            Run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = repoId,
                    PrId = "7",
                    HeadSha = "head",
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = "collect-only",
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Pending,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );
            Directory.CreateDirectory(_tempRoot);
            RunDirectoryRoot = Path.Combine(_tempRoot, "runs");
            WorkflowStore = workflowStore ?? new InMemoryWorkflowStore();
            Pool = new FakePool(new ReviewSlot("widgets", 0));
            Sessions = new FakeReviewSessionProvisioner(new ReviewRunSession("session-1", CommandRunner, Files));
            Workspace = CreateWorkspace();
        }

        public ReviewStore Store { get; }
        public string DatabasePath => _database.Path;
        public ReviewRun Run { get; }
        public IWorkflowStore WorkflowStore { get; }
        public string RunDirectoryRoot { get; }
        public FakePool Pool { get; }
        public FakePreparer Preparer { get; } = new();
        public FakeSandboxFileSystem Files { get; } = new();
        public FakeSandboxCommandRunner CommandRunner { get; } = new();
        public FakeReviewSessionProvisioner Sessions { get; }
        public WorkflowWorkspace Workspace { get; }
        public int InvokerFactoryCalls { get; private set; }

        public ReviewWorkflowRunner CreateRunner(
            Func<ReviewRun, string, string, CancellationToken, Task<IWorkflowTaskInvoker>> factory,
            Func<ReviewRun, WorkflowRound?, IReadOnlyDictionary<string, string>>? bindings = null,
            WorkflowWorkspace? workspace = null,
            TimeSpan? invocationTimeout = null,
            bool resetFailedAttempts = false
        ) =>
            new(
                WorkflowPath(),
                RunDirectoryRoot,
                Store,
                WorkflowStore,
                workspace ?? Workspace,
                async (run, instanceId, directory, cancellationToken) =>
                {
                    InvokerFactoryCalls++;
                    return await factory(run, instanceId, directory, cancellationToken);
                },
                bindings,
                invocationTimeout,
                resetFailedAttempts: resetFailedAttempts
            );

        public WorkflowRound CreateRound(string route, JsonObject frozen) =>
            Store.CreateOrGetWorkflowRound(
                new WorkflowRoundSeed
                {
                    ReviewRunId = Run.Id,
                    RepoId = Run.RepoId,
                    PrId = Run.PrId,
                    HeadSha = Run.HeadSha,
                    Kind = route == "discussion" ? WorkflowRoundKind.Discussion : WorkflowRoundKind.Merged,
                    EventKey = route == "discussion" ? "messages-108-112" : "merge-sha",
                    FrozenInputJson = frozen.ToJsonString(),
                }
            );

        public ReviewRun CreateRun(string prId, string headSha) =>
            Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = Run.RepoId,
                    PrId = prId,
                    HeadSha = headSha,
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = "collect-only",
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Pending,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );

        public WorkflowWorkspace CreateWorkspace(IReviewSlotPool? pool = null) =>
            new(
                Store,
                new CodeReviewDaemonOptions(),
                new ReviewSlotWorkspace(pool ?? Pool, _ => Preparer),
                Sessions,
                (_, _, _) => throw new InvalidOperationException("runner must not prepare the workspace"),
                NullLoggerFactory.Instance
            );

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
            try
            {
                if (Directory.Exists(_tempRoot))
                    Directory.Delete(_tempRoot, recursive: true);
            }
            catch { }
        }
    }

    private sealed class RecordingInvoker : IWorkflowTaskInvoker
    {
        public bool ContainmentFailure { get; init; }
        public string? UnknownTask { get; init; }
        public string? UnknownReconcileTask { get; set; }
        public string? FailedTask { get; init; }
        public string TerminalState { get; init; } = "Merged";
        public List<WorkflowInvocation> Invoked { get; } = [];
        public List<WorkflowInvocation> Reconciled { get; } = [];

        public Task<WorkflowInvocationResult> InvokeAsync(WorkflowInvocation invocation, CancellationToken ct = default)
        {
            if (ContainmentFailure)
                throw new WorkflowScriptTerminationException("injected containment uncertainty");
            Invoked.Add(invocation);
            return Task.FromResult(Result(invocation));
        }

        public Task<WorkflowInvocationResult> ReconcileAsync(
            WorkflowInvocation invocation,
            CancellationToken ct = default
        )
        {
            Reconciled.Add(invocation);
            return Task.FromResult(
                Step(invocation) == UnknownReconcileTask
                    ? new WorkflowInvocationResult(WorkflowInvocationStatus.Unknown)
                    : Success(Step(invocation))
            );
        }

        private WorkflowInvocationResult Result(WorkflowInvocation invocation)
        {
            var stepId = Step(invocation);
            return stepId == UnknownTask ? new WorkflowInvocationResult(WorkflowInvocationStatus.Unknown)
                : stepId == FailedTask ? new WorkflowInvocationResult(WorkflowInvocationStatus.Failed, Error: "failed")
                : Success(stepId);
        }

        private WorkflowInvocationResult Success(string taskId) =>
            new(
                WorkflowInvocationStatus.Completed,
                taskId switch
                {
                    "prepare-independent" =>
                        """{"PrId":"7","HeadSha":"head","WindowId":"window","ContextArtifact":"context.json","PublicationMode":"collect_only"}""",
                    "prepare-discussion" =>
                        """{"PrId":"7","HeadSha":"head","WindowId":"window","ContextArtifact":"context.json"}""",
                    "prepare-history" => new JsonObject
                    {
                        ["PrId"] = "7",
                        ["HeadSha"] = "head",
                        ["WindowId"] = "window",
                        ["ContextArtifact"] = "context.json",
                        ["TerminalState"] = TerminalState,
                    }.ToJsonString(),
                    "independent-review"
                    or "independent-grade"
                    or "collect-comments"
                    or "assess-performance"
                    or "final-review" => """{"Format":"markdown","Markdown":"Review complete."}""",
                    "fetch-discussion" =>
                        """{"ReviewRunId":1,"HeadSha":"head","CapturedAt":"2026-01-01T00:00:00Z","CommentBaseline":[],"CommentWindow":[]}""",
                    "collect-publication" => """{"Outcome":"no_op","Description":"collect only","Actions":[]}""",
                    "review" => """{"Findings":[],"ReviewText":"review"}""",
                    "grade" => """{"Assessments":[],"Description":"grade"}""",
                    "publish" => """{"Outcome":"no_op","Description":"nothing to publish","Actions":[]}""",
                    "discussion" => """{"Outcome":"no_op","Description":"nothing to reply","Actions":[]}""",
                    "learnings" => """{"Edits":[],"Description":"none"}""",
                    "merged-judgment" =>
                        """{"Verdict":"approved","Reviewed":{"Edits":[],"Description":"none"},"Description":"Evidence-backed judgment."}""",
                    "merged-self-assessment" => """{"Format":"markdown","Markdown":"Evidence-backed assessment."}""",
                    "collect-statistics" =>
                        """{"RunId":"7","ArtifactCount":0,"ArtifactCountsByKind":{},"ReceiptCount":0,"ReceiptCountsByStatus":{}}""",
                    "retain-independent" or "retain-review" or "retain-discussion" or "retain-merged" =>
                        """{"ArtifactBranch":"artifacts/7","RetainedSha":"abc"}""",
                    "close-artifact-branch" => """{"ArtifactBranch":"artifacts/7","Closed":true}""",
                    _ => throw new InvalidOperationException($"No result for task '{taskId}'."),
                }
            );
    }

    private static string Step(WorkflowInvocation invocation) =>
        invocation.Task.Id.EndsWith(":task", StringComparison.Ordinal)
            ? invocation.Task.Id[..^":task".Length]
            : invocation.Task.Id;

    private sealed class FailingWorkflowStore : IWorkflowStore
    {
        private WorkflowInstanceSnapshot? _last;

        public Task SaveAsync(string instanceId, WorkflowInstanceSnapshot snapshot, CancellationToken ct = default)
        {
            if (snapshot.Tasks.Any(value => value.Status == WorkflowTaskStatus.Validated))
            {
                throw new IOException("simulated save failure after invocation");
            }
            _last = snapshot.DeepCopy();
            return Task.CompletedTask;
        }

        public Task<WorkflowInstanceSnapshot?> LoadAsync(string instanceId, CancellationToken ct = default) =>
            Task.FromResult(_last?.DeepCopy());

        public Task DeleteAsync(string instanceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakePool(ReviewSlot slot) : IReviewSlotPool
    {
        public bool Available { get; set; } = true;
        public int Leased { get; private set; }
        public int Recovered { get; private set; }
        public int Preferred { get; private set; }
        public int Returned { get; private set; }
        public int Retired { get; private set; }

        public IReadOnlyList<ReviewSlot> Slots => [slot];

        public Task<ReviewSlot> LeaseAsync(string repositoryName, CancellationToken cancellationToken)
        {
            Available = false;
            Leased++;
            return Task.FromResult(slot);
        }

        public Task<ReviewSlot?> TryLeaseAsync(string repositoryName, CancellationToken cancellationToken)
        {
            if (!Available)
                return Task.FromResult<ReviewSlot?>(null);
            Available = false;
            Leased++;
            return Task.FromResult<ReviewSlot?>(slot);
        }

        public Task<ReviewSlot?> TryLeasePreferredAsync(ReviewSlot value, CancellationToken cancellationToken)
        {
            if (!Available)
                return Task.FromResult<ReviewSlot?>(null);
            Available = false;
            Preferred++;
            return Task.FromResult<ReviewSlot?>(value);
        }

        public Task<ReviewSlot> LeasePreferredAsync(ReviewSlot value, CancellationToken cancellationToken)
        {
            Available = false;
            Preferred++;
            return Task.FromResult(value);
        }

        public Task<ReviewSlot> RecoverLeaseAsync(ReviewSlot value, CancellationToken cancellationToken)
        {
            Available = false;
            Recovered++;
            return Task.FromResult(value);
        }

        public Task ReturnAsync(ReviewSlot value, CancellationToken cancellationToken)
        {
            Available = true;
            Returned++;
            return Task.CompletedTask;
        }

        public Task RetireAsync(ReviewSlot value, CancellationToken cancellationToken)
        {
            Retired++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePreparer : IReviewSlotPreparer
    {
        public int Calls { get; private set; }

        public Task<PreparedCheckout> PrepareAsync(
            ReviewRun run,
            ReviewSlot slot,
            RepoIdentity repository,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            throw new InvalidOperationException("runner must not prepare the workspace");
        }
    }

    private static string WorkflowPath([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(sourcePath)!,
                "../../../samples/CodeReviewDaemon.Sample/.review/workflow.yaml"
            )
        );
}
