using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task #82 requirement 4 — the ONLY path that removes a retained review-artifact branch. It is explicit
/// (never scheduled, never reached from the review flow), it deletes exactly the one branch its own durable
/// receipt names at exactly the SHA that receipt recorded, and an outcome it cannot read as
/// "gone" quarantines the run instead of retrying.
/// <para>
/// Requirement 6 is visible in the signature rather than in an assertion: this command takes no slot pool,
/// no workspace and no assignment, so source-slot cleanup/release remains entirely where it already was.
/// </para>
/// </summary>
public sealed class RedoReviewArtifactBranchCommandTests
{
    [Fact]
    public async Task Refuses_when_no_branch_receipt_was_ever_recorded()
    {
        using var fixture = new Fixture(recordBranchReceipt: false);

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.NoRecordedBranch);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_the_recorded_branch_is_not_this_pull_requests_branch()
    {
        using var fixture = new Fixture(branchOverride: "review/widgets-9");

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.OwnershipMismatch);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_the_recorded_sha_disagrees_with_the_retention_receipt()
    {
        using var fixture = new Fixture(receiptShaOverride: Fixture.OtherSha);

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.OwnershipMismatch);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_while_a_workflow_workspace_assignment_is_still_active()
    {
        using var fixture = new Fixture();
        fixture.MarkWorkspaceActive();

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.ActiveWorkflow);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(OutboxStatus.Pending))]
    [InlineData(nameof(OutboxStatus.Sending))]
    [InlineData(nameof(OutboxStatus.Leased))]
    public async Task Refuses_while_any_operation_for_the_run_is_unresolved(string status)
    {
        using var fixture = new Fixture();
        fixture.EnqueueUnresolvedOperation(Enum.Parse<OutboxStatus>(status));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.UnresolvedOperation);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_when_the_remote_branch_no_longer_points_at_the_recorded_sha()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(0, $"{Fixture.OtherSha}\trefs/heads/{Fixture.Branch}", ""));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.RemoteDrift);
        fixture.Runner.Commands.Should().NotContain(command => command.Argv.Contains("--delete"));
        fixture.Store.GetOutboxForRun(fixture.Run.Id)[0].Status.Should().Be(OutboxStatus.Posted);
    }

    [Fact]
    public async Task Deletes_exactly_that_branch_and_invalidates_the_receipt_without_re_admitting_the_run()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Deleted);
        result.Branch.Should().Be(Fixture.Branch);
        result.RecordedSha.Should().Be(Fixture.Sha);

        var deletions = fixture
            .Runner.Commands.Where(command => command.Argv.Contains("--delete") || command.Argv.Contains("-D"))
            .ToList();
        deletions.Should().HaveCount(2);
        deletions.Should().OnlyContain(command => command.Argv.Contains(Fixture.Branch));

        // Remote FIRST, local second. The remote ref is the published artifact and the only one whose
        // removal is guarded; dropping the local ref before that is proven gone would discard the one local
        // handle on a branch whose deletion may have just been refused.
        deletions[0].Argv.Should().Contain("--delete");
        deletions[1].Argv.Should().Contain("-D");

        // The compare-and-swap is what makes this safe, so pin that it was actually sent: receive-pack
        // compares this expected old value to the ref under its own lock and rejects a stale update.
        deletions[0]
            .Argv.Should()
            .Contain(ReviewBranchManager.DeleteExpectedShaArgument(Fixture.Branch, Fixture.Sha));

        var receipt = fixture.RetentionReceipts().Single();
        receipt.Status.Should().Be(OutboxStatus.Pending);
        receipt.ProviderResponseId.Should().BeNull();

        // Deletion and re-admission are separate operator intents: this command never re-reads the PR, so
        // it must not put the run back in the pipeline against an identity nobody re-validated.
        var run = fixture.Store.GetReviewRun(fixture.Run.Id)!;
        run.Stage.Should().Be(ReviewStage.Posted);
        run.WorkflowStatus.Should().Be(WorkflowStatus.Completed);

        fixture
            .Store.TryGetLatestArtifact(fixture.Run.Id, ReviewArtifactKinds.ArtifactBranchRedoKind)
            .Should()
            .NotBeNull();
    }

    /// <summary>
    /// The durable half of "delete, then re-review". This command cannot re-admit the run itself — it never
    /// contacts the provider, so it cannot revalidate the PR's identity — and the one-shot run command
    /// cannot tell a completed review from a completed review whose output was deliberately destroyed. The
    /// authorization is that missing fact, written by the side that established it.
    /// </summary>
    [Fact]
    public async Task A_verified_deletion_records_a_one_use_rerun_authorization()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Deleted);
        result.RerunAuthorizationId.Should().NotBeNull();
        result.RerunWatermark.Should().NotBeNullOrWhiteSpace();

        var authorization = fixture.Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7");
        authorization.Should().NotBeNull();
        authorization!.Id.Should().Be(result.RerunAuthorizationId);
        authorization.IsOutstanding.Should().BeTrue();
        authorization.PriorReviewRunId.Should().Be(fixture.Run.Id);
        authorization.DeletedBranch.Should().Be(Fixture.Branch);
        authorization.DeletedBranchSha.Should().Be(Fixture.Sha);
        authorization.Describes(Fixture.Sha, "base").Should().BeTrue();
        // The watermark is what lets the new run be a NEW review_run row: the table is unique over
        // (repo, pr, head, base, watermark, kind, variant, mode) and a rerun differs on nothing else.
        authorization.RerunWatermark.Should().NotBe(fixture.Run.TriggerWatermark);

        // The superseded run is evidence and stays exactly as it was.
        var prior = fixture.Store.GetReviewRun(fixture.Run.Id)!;
        prior.Stage.Should().Be(ReviewStage.Posted);
        prior.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        prior.TriggerWatermark.Should().Be(fixture.Run.TriggerWatermark);
    }

    /// <summary>
    /// The safety property in one test: an outcome nobody could read as "gone" authorizes nothing. If it
    /// did, a rerun would push a second artifact history onto a branch that may still be published.
    /// </summary>
    [Fact]
    public async Task A_quarantined_redo_authorizes_no_rerun()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(0, $"{Fixture.Sha}\trefs/heads/{Fixture.Branch}", ""));
        fixture.Runner.OnArgvContains("--delete", new SandboxCommandResult(1, "", "remote rejected"));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Quarantined);
        result.RerunAuthorizationId.Should().BeNull();
        fixture.Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7").Should().BeNull();
    }

    [Fact]
    public async Task A_drifted_remote_authorizes_no_rerun()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(0, $"{Fixture.OtherSha}\trefs/heads/{Fixture.Branch}", ""));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.RemoteDrift);
        fixture.Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7").Should().BeNull();
    }

    /// <summary>
    /// One outstanding authorization per pull request, enforced by a partial unique index rather than by a
    /// check-then-insert with a window in it. A second redo of the same PR does not mint a second token.
    /// </summary>
    [Fact]
    public async Task A_second_redo_does_not_mint_a_second_outstanding_authorization()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();
        var first = await fixture.RunAsync();

        var second = fixture.Store.TryRecordRerunAuthorization(
            fixture.Run.RepoId,
            "7",
            Fixture.Sha,
            "base",
            fixture.Run.Id,
            Fixture.Branch,
            Fixture.OtherSha
        );

        second.Should().BeNull();
        fixture
            .Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7")!
            .Id.Should()
            .Be(first.RerunAuthorizationId);
    }

    /// <summary>
    /// The claim re-checks the identity inside the same statement that consumes the row. A caller that
    /// validated the head and then marked the row consumed would have a window in which the PR could be
    /// pushed to, and would re-review an identity nobody checked.
    /// </summary>
    [Fact]
    public async Task An_authorization_is_claimed_once_and_only_for_the_identity_it_names()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();
        var result = await fixture.RunAsync();
        var id = result.RerunAuthorizationId!.Value;
        // Stands in for the run a consumer would create; it only has to be a real review_run row, because
        // consumed_by_run_id is a foreign key.
        var consumer = fixture.Run.Id;

        // The PR moved since the redo: same row, different head. The claim must fail.
        fixture.Store.TryConsumeRerunAuthorization(id, Fixture.OtherSha, "base", consumer).Should().BeFalse();
        fixture.Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7").Should().NotBeNull();

        fixture.Store.TryConsumeRerunAuthorization(id, Fixture.Sha, "base", consumer).Should().BeTrue();
        // One use. A replayed consume — a retry, a second process — claims nothing.
        fixture.Store.TryConsumeRerunAuthorization(id, Fixture.Sha, "base", consumer).Should().BeFalse();
        fixture.Store.GetOutstandingRerunAuthorization(fixture.Run.RepoId, "7").Should().BeNull();
    }

    [Fact]
    public async Task An_already_absent_remote_reconciles_without_issuing_a_deletion()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(2, "", ""));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.AlreadyAbsent);
        fixture.Runner.Commands.Should().NotContain(command => command.Argv.Contains("--delete"));
        fixture.Store.GetOutboxForRun(fixture.Run.Id).Single().ProviderResponseId.Should().BeNull();
        fixture.Store.GetReviewRun(fixture.Run.Id)!.Stage.Should().Be(ReviewStage.Posted);
    }

    [Fact]
    public async Task An_unreadable_remote_is_never_treated_as_absence()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(128, "", "fatal: could not read Username"));

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Quarantined);
        fixture.Runner.Commands.Should().NotContain(command => command.Argv.Contains("--delete"));
    }

    [Fact]
    public async Task An_unknown_deletion_outcome_quarantines_the_run_and_a_repeat_never_retries_git()
    {
        using var fixture = new Fixture();
        fixture.ScriptRemote(new SandboxCommandResult(0, $"{Fixture.Sha}\trefs/heads/{Fixture.Branch}", ""));
        fixture.Runner.OnArgvContains("--delete", new SandboxCommandResult(1, "", "remote rejected"));

        var first = await fixture.RunAsync();

        first.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Quarantined);
        fixture.Store.GetReviewRun(fixture.Run.Id)!.ParkedAt.Should().NotBeNull();
        var quarantine = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            ReviewArtifactKinds.ArtifactBranchQuarantineKind
        );
        quarantine.Should().NotBeNull();
        // An operator reconciling by hand needs to know whether the branch may still be published. Here
        // nothing established that, so it must NOT say "deleted".
        JsonNode.Parse(quarantine!.Payload)!["BranchState"]!
            .GetValue<string>()
            .Should()
            .Be("unknown");
        // The receipt is NOT invalidated: nothing proved the pushed branch is gone.
        fixture.RetentionReceipts().Single().ProviderResponseId.Should().Be(Fixture.Sha);

        var issued = fixture.Runner.Commands.Count;
        var second = await fixture.RunAsync();

        second.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Quarantined);
        fixture.Runner.Commands.Should().HaveCount(issued);
    }

    /// <summary>
    /// The coordinator lease is the daemon's own, held for its entire lifetime — so a daemon that is
    /// running (and therefore polling) makes every gate below it a stale read. The refusal is structured,
    /// not an exception, and it happens before a single row is read or a single git command is issued.
    /// </summary>
    [Fact]
    public async Task Refuses_before_reading_anything_while_the_workflow_coordinator_lease_is_held()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();
        using var daemonLease = WorkflowCoordinatorLease.Acquire(fixture.DatabasePath);

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.CoordinatorLeaseUnavailable);
        result.ReviewRunId.Should().BeNull();
        fixture.Runner.Commands.Should().BeEmpty();
        fixture.RetentionReceipts().Single().ProviderResponseId.Should().Be(Fixture.Sha);
    }

    [Fact]
    public async Task The_lease_is_released_so_a_redo_succeeds_once_the_daemon_has_stopped()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();

        using (var daemonLease = WorkflowCoordinatorLease.Acquire(fixture.DatabasePath))
        {
            (await fixture.RunAsync()).Outcome.Should().Be(RedoReviewArtifactBranchOutcome.CoordinatorLeaseUnavailable);
        }

        (await fixture.RunAsync()).Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Deleted);
    }

    /// <summary>
    /// A run accumulates ONE retention receipt per workflow instance — the idempotency key includes the
    /// instance id — so asking for "the" retention row with <c>SingleOrDefault</c> threw
    /// <see cref="InvalidOperationException"/> out of an operator command on ordinary state. The receipt is
    /// now identified by reconstructing the key of the instance the branch receipt names.
    /// </summary>
    [Fact]
    public async Task Several_workflow_instances_retaining_the_same_commit_do_not_crash_the_command()
    {
        using var fixture = new Fixture();
        var sibling = fixture.PostRetention(Fixture.SecondInstanceId, Fixture.Sha);
        fixture.ScriptSuccessfulDeletion();

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Deleted);
        // Both attest to the commit that was just deleted, so BOTH lose their acknowledgement; leaving the
        // sibling Posted would let a later retention short-circuit onto a commit that no longer exists.
        fixture.RetentionReceipts().Should().HaveCount(2);
        fixture.RetentionReceipts().Should().OnlyContain(entry => entry.ProviderResponseId == null);
        fixture.RetentionReceipts().Should().OnlyContain(entry => entry.Status == OutboxStatus.Pending);
        _ = sibling;
    }

    [Fact]
    public async Task Refuses_when_another_workflow_instance_retained_a_different_commit_on_the_branch()
    {
        using var fixture = new Fixture();
        _ = fixture.PostRetention(Fixture.SecondInstanceId, Fixture.OtherSha);
        fixture.ScriptSuccessfulDeletion();

        var result = await fixture.RunAsync();

        // Deleting the branch would discard the other instance's retention too, and nothing here can decide
        // that is acceptable. Refuse, touch nothing.
        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.AmbiguousRetention);
        fixture.Runner.Commands.Should().BeEmpty();
        fixture.RetentionReceipts().Should().OnlyContain(entry => entry.Status == OutboxStatus.Posted);
    }

    [Fact]
    public async Task Refuses_when_no_retention_receipt_belongs_to_the_recorded_workflow_instance()
    {
        using var fixture = new Fixture(workflowInstanceOverride: "an-instance-that-never-retained");

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.OwnershipMismatch);
        fixture.Runner.Commands.Should().BeEmpty();
    }

    /// <summary>
    /// The branch IS gone and the conditional invalidation did not apply — the row moved between the gate
    /// that read it and the write. Reporting success would leave a receipt that can short-circuit a later
    /// retention onto a deleted commit, with nothing recording that we knew.
    /// </summary>
    [Fact]
    public async Task A_receipt_mutated_during_the_deletion_quarantines_rather_than_reporting_success()
    {
        using var fixture = new Fixture();
        fixture.ScriptSuccessfulDeletion();
        // Fires while the guarded delete is in flight: exactly the concurrent-mutation window.
        fixture.Runner.BeforeArgvContains(
            "--delete",
            () => fixture.Store.TryTransitionOutbox(fixture.Retention.Id, OutboxStatus.Posted, OutboxStatus.Collected)
        );

        var result = await fixture.RunAsync();

        result.Outcome.Should().Be(RedoReviewArtifactBranchOutcome.Quarantined);
        result.Detail.Should().Contain("could not be invalidated");
        var quarantine = fixture.Store.TryGetLatestArtifact(
            fixture.Run.Id,
            ReviewArtifactKinds.ArtifactBranchQuarantineKind
        );
        // Truthful in the OTHER direction: the branch really is gone, and saying "unknown" here would send
        // an operator looking for a ref that no longer exists.
        JsonNode.Parse(quarantine!.Payload)!["BranchState"]!
            .GetValue<string>()
            .Should()
            .Be("deleted");
        fixture.Store.GetReviewRun(fixture.Run.Id)!.ParkedAt.Should().NotBeNull();
    }

    [Fact]
    public void Invalidating_a_receipt_twice_applies_exactly_once()
    {
        using var fixture = new Fixture();

        fixture
            .Store.TryInvalidateOutboxReceipt(fixture.Retention.Id, OutboxStatus.Posted, Fixture.Sha)
            .Should()
            .BeTrue();
        fixture
            .Store.TryInvalidateOutboxReceipt(fixture.Retention.Id, OutboxStatus.Posted, Fixture.Sha)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Invalidating_a_receipt_that_acknowledged_a_different_commit_does_not_apply()
    {
        using var fixture = new Fixture();

        fixture
            .Store.TryInvalidateOutboxReceipt(fixture.Retention.Id, OutboxStatus.Posted, Fixture.OtherSha)
            .Should()
            .BeFalse();
        fixture.RetentionReceipts().Single().ProviderResponseId.Should().Be(Fixture.Sha);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public const string Branch = "review/widgets-7";
        public const string InstanceId = "workflow-instance-1";
        public const string SecondInstanceId = "workflow-instance-2";

        private readonly TempSqliteDatabase _db = new();
        private readonly string _repoRoot;
        private readonly RepoIdentity _repo = new()
        {
            Provider = "github",
            OrgOrOwner = "example",
            RepoName = "widgets",
        };

        public ReviewStore Store { get; }
        public ReviewRun Run { get; }
        public FakeSandboxCommandRunner Runner { get; } = new();

        /// <summary>The retention receipt the recorded branch belongs to.</summary>
        public OutboxEntry Retention { get; private set; } = null!;

        public string DatabasePath => _db.Path;

        public Fixture(
            bool recordBranchReceipt = true,
            string? branchOverride = null,
            string? receiptShaOverride = null,
            string? workflowInstanceOverride = null
        )
        {
            // A real directory: the command takes the host retention checkout's repository lock, which
            // creates a sibling lock file next to the repo root.
            _repoRoot = Path.Combine(Path.GetTempPath(), "crd-redo-" + Guid.NewGuid().ToString("N"), "store");
            _ = Directory.CreateDirectory(_repoRoot);
            Store = new ReviewStore(_db.ConnectionString);
            var repoId = Store.EnsureRepo(_repo);
            Run = Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = repoId,
                    PrId = "7",
                    HeadSha = Sha,
                    BaseSha = "base",
                    TriggerWatermark = "1",
                    ReviewKind = "new_head",
                    VariantId = "primary",
                    Mode = "collect-only",
                    Stage = ReviewStage.Posted,
                    WorkflowStatus = WorkflowStatus.Completed,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );
            if (!recordBranchReceipt)
                return;
            Retention = PostRetention(InstanceId, receiptShaOverride ?? Sha);
            Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = Run.Id,
                    ArtifactKind = ReviewArtifactKinds.ArtifactBranchKind,
                    ArtifactSchemaVersion = ReviewArtifactKinds.ArtifactBranchSchemaVersion,
                    Provider = "github",
                    Payload = JsonSerializer.Serialize(
                        new ReviewArtifactBranchReceipt(
                            branchOverride ?? Branch,
                            Sha,
                            _repo.NormalizedKey,
                            "7",
                            Sha,
                            workflowInstanceOverride ?? InstanceId
                        )
                    ),
                }
            );
        }

        /// <summary>
        /// Enqueues and acknowledges a retention receipt for one workflow instance, keyed exactly as
        /// production keys it. A run legitimately accumulates one of these PER INSTANCE, which is why the
        /// command cannot ask for "the" retention row.
        /// </summary>
        public OutboxEntry PostRetention(string workflowInstanceId, string pushedSha)
        {
            var entry = Store.EnqueueOutbox(
                new OutboxEntry
                {
                    IdempotencyKey = WorkflowArtifactOperations.BuildRetentionKey(_repo, Run, workflowInstanceId),
                    Provider = "github",
                    ReviewRunId = Run.Id,
                    Operation = WorkflowArtifactOperations.RetentionOperation,
                    ArtifactKind = "workflow-artifacts",
                    Status = OutboxStatus.Pending,
                    BodyHash = "hash-" + workflowInstanceId,
                }
            );
            _ = Store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, OutboxStatus.Posted, pushedSha);
            return entry;
        }

        public void MarkWorkspaceActive() =>
            Store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = Run.Id,
                    ArtifactKind = "workflow-workspace-assignment",
                    ArtifactSchemaVersion = 1,
                    Provider = "github",
                    Payload = new JsonObject { ["Active"] = true, ["AssignmentId"] = "a" }.ToJsonString(),
                }
            );

        public void EnqueueUnresolvedOperation(OutboxStatus status)
        {
            var entry = Store.EnqueueOutbox(
                new OutboxEntry
                {
                    IdempotencyKey = "v1:other:7",
                    Provider = "github",
                    ReviewRunId = Run.Id,
                    Operation = "post-review-comment",
                    ArtifactKind = "review",
                    Status = OutboxStatus.Pending,
                    BodyHash = "hash",
                }
            );
            if (status != OutboxStatus.Pending)
                Store.TryTransitionOutbox(entry.Id, OutboxStatus.Pending, status);
        }

        public void ScriptRemote(SandboxCommandResult result) => Runner.OnArgvContains("ls-remote", result);

        public void ScriptSuccessfulDeletion()
        {
            Runner.OnArgvContainsSequence(
                "ls-remote",
                new SandboxCommandResult(0, $"{Sha}\trefs/heads/{Branch}", ""),
                new SandboxCommandResult(2, "", "")
            );
            Runner.OnArgvContains("--delete", new SandboxCommandResult(0, "", ""));
        }

        public IReadOnlyList<OutboxEntry> RetentionReceipts() =>
            [
                .. Store
                    .GetOutboxForRun(Run.Id)
                    .Where(entry => entry.Operation == WorkflowArtifactOperations.RetentionOperation),
            ];

        public Task<RedoReviewArtifactBranchResult> RunAsync() =>
            RedoReviewArtifactBranchCommand.RunAsync(
                Store,
                _repo,
                "7",
                _repoRoot,
                DatabasePath,
                new ReviewBranchManager(
                    new GitRunner(Runner),
                    new FakeSandboxFileSystem(),
                    NullLogger<ReviewBranchManager>.Instance
                ),
                new HostGitPushAuthorization(allowAllPushes: true),
                NullLogger.Instance,
                TimeProvider.System,
                CancellationToken.None
            );

        public void Dispose()
        {
            Store.Dispose();
            _db.Dispose();
            try
            {
                Directory.Delete(Path.GetDirectoryName(_repoRoot)!, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // best-effort temp cleanup
            }
        }
    }
}
