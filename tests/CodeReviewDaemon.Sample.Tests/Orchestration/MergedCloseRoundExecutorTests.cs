using System.Security.Cryptography;
using System.Text;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class MergedCloseRoundExecutorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Retry_resumes_at_the_first_missing_durable_substep()
    {
        using var fixture = new Fixture();
        var completed = new List<MergedCloseSubstep>();
        var failOnce = true;
        var executor = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                completed.Add(step);
                if (step == MergedCloseSubstep.Verified && failOnce)
                {
                    failOnce = false;
                    return MergedCloseSubstepResult.Retry;
                }

                return await fixture.CompleteForRealAsync(round, step, token);
            }
        );

        var first = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);
        var second = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        first.Should().Be(EngagementRoundStatus.RetryPending);
        second.Should().Be(EngagementRoundStatus.Completed);
        completed
            .Should()
            .Equal(
                MergedCloseSubstep.EvidenceFrozen,
                MergedCloseSubstep.CandidatesBuilt,
                MergedCloseSubstep.Classified,
                MergedCloseSubstep.Verified,
                MergedCloseSubstep.Verified,
                MergedCloseSubstep.KnowledgePromoted,
                MergedCloseSubstep.DeveloperLearningPromoted,
                MergedCloseSubstep.ReportsWritten,
                MergedCloseSubstep.Archived
            );
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Where(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType)
            .OrderBy(record => record.Sequence)
            .Select(record => record.GenerationId)
            .Should()
            .Equal(Enum.GetNames<MergedCloseSubstep>());
    }

    /// <summary>
    /// Every checkpoint carries the artifact its substep produced, not the substep's own name. Two
    /// substeps that did different work therefore cannot share a receipt hash.
    /// </summary>
    [Fact]
    public async Task Checkpoint_content_is_the_artifact_the_substep_produced()
    {
        using var fixture = new Fixture();
        var executor = fixture.CreateExecutor(fixture.CompleteForRealAsync);

        (await executor.ExecuteAsync(fixture.Round, CancellationToken.None))
            .Should()
            .Be(EngagementRoundStatus.Completed);

        var frozen = fixture.Store.ReadAuditContent(
            MergedCloseReceiptLedger.RecordId(fixture.Round.Id, MergedCloseSubstep.EvidenceFrozen)
        );
        MergedCloseEvidenceFreezer.TryDeserialize(frozen)!.CloseRoundId.Should().Be(fixture.Round.Id);
        frozen.Should().NotEqual(Encoding.UTF8.GetBytes(MergedCloseSubstep.EvidenceFrozen.ToString()));
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Where(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType)
            .Select(record => record.ContentSha256)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .HaveCount(Enum.GetValues<MergedCloseSubstep>().Length);
    }

    /// <summary>
    /// A handler that reports success without producing anything has shown no evidence that it ran, so
    /// it must be indistinguishable from a retry: no checkpoint, and the chain does not advance.
    /// </summary>
    [Fact]
    public async Task Substep_that_claims_completion_without_an_artifact_is_not_checkpointed()
    {
        using var fixture = new Fixture();
        var invoked = new List<MergedCloseSubstep>();
        var executor = fixture.CreateExecutor(
            (_, step, _) =>
            {
                invoked.Add(step);
                return Task.FromResult(
                    new MergedCloseSubstepResult(MergedCloseSubstepOutcome.Completed, Receipt: null)
                );
            }
        );

        var outcome = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.RetryPending);
        invoked.Should().Equal(MergedCloseSubstep.EvidenceFrozen);
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType);
    }

    /// <summary>A receipt cannot be constructed from nothing, so "completed with no artifact" is unrepresentable.</summary>
    [Fact]
    public void Receipt_rejects_empty_content()
    {
        var act = () => new MergedCloseSubstepReceipt(ReadOnlyMemory<byte>.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Restart_uses_persisted_substeps_instead_of_repeating_completed_work()
    {
        using var reports = new TempReportRoot();
        using var database = new TempSqliteDatabase();
        long roundId;
        using (var firstStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(Start)))
        {
            var seeded = SeedRound(firstStore);
            roundId = seeded.Id;
            var firstChain = new SubstepChain(firstStore, reports.Path);
            var first = new MergedCloseRoundExecutor(
                firstStore,
                async (round, step, token) =>
                    step == MergedCloseSubstep.Classified
                        ? MergedCloseSubstepResult.Retry
                        : await firstChain.CompleteAsync(round, step, token),
                new FakeTimeProvider(Start)
            );

            (await first.ExecuteAsync(firstStore.GetEngagementRound(roundId)!, CancellationToken.None))
                .Should()
                .Be(EngagementRoundStatus.RetryPending);
        }

        using var restartedStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(Start));
        var afterRestart = new List<MergedCloseSubstep>();
        var restartedChain = new SubstepChain(restartedStore, reports.Path);
        var restarted = new MergedCloseRoundExecutor(
            restartedStore,
            async (round, step, token) =>
            {
                afterRestart.Add(step);
                return await restartedChain.CompleteAsync(round, step, token);
            },
            new FakeTimeProvider(Start)
        );

        (await restarted.ExecuteAsync(restartedStore.GetEngagementRound(roundId)!, CancellationToken.None))
            .Should()
            .Be(EngagementRoundStatus.Completed);
        afterRestart.Should().StartWith(MergedCloseSubstep.Classified);
        afterRestart.Should().NotContain(MergedCloseSubstep.EvidenceFrozen);
        afterRestart.Should().NotContain(MergedCloseSubstep.CandidatesBuilt);
    }

    /// <summary>
    /// The frozen inventory is the one thing every later substep reasons over, so a restart must reuse
    /// the exact bytes it froze rather than re-freezing a target that has since moved.
    /// </summary>
    [Fact]
    public async Task Frozen_evidence_survives_restart_byte_identical_and_is_not_refrozen()
    {
        using var reports = new TempReportRoot();
        using var database = new TempSqliteDatabase();
        long roundId;
        byte[] frozenBefore;
        using (var firstStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(Start)))
        {
            roundId = SeedRound(firstStore).Id;
            var chain = new SubstepChain(firstStore, reports.Path);
            var executor = new MergedCloseRoundExecutor(
                firstStore,
                async (round, step, token) =>
                    step == MergedCloseSubstep.Classified
                        ? MergedCloseSubstepResult.Retry
                        : await chain.CompleteAsync(round, step, token),
                new FakeTimeProvider(Start)
            );

            _ = await executor.ExecuteAsync(firstStore.GetEngagementRound(roundId)!, CancellationToken.None);
            frozenBefore = firstStore.ReadAuditContent(
                MergedCloseReceiptLedger.RecordId(roundId, MergedCloseSubstep.EvidenceFrozen)
            );
        }

        using var restartedStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(Start.AddDays(1)));
        // A source record that appeared after the freeze WOULD change the manifest if it were rebuilt, so
        // the byte-identical assertion below can only hold because the restart re-read the frozen one.
        StoreEvidenceRecord(restartedStore, roundId, "late-source", Start.AddDays(1));
        var refrozen = new List<MergedCloseSubstep>();
        var restartedChain = new SubstepChain(restartedStore, reports.Path);
        // Non-vacuity: rebuilding the manifest now really does produce different bytes, so the
        // byte-identical assertion at the end is a claim about re-reading, not a tautology.
        restartedChain.FreezeBytes(restartedStore.GetEngagementRound(roundId)!).Should().NotEqual(frozenBefore);
        var restarted = new MergedCloseRoundExecutor(
            restartedStore,
            async (round, step, token) =>
            {
                refrozen.Add(step);
                return await restartedChain.CompleteAsync(round, step, token);
            },
            new FakeTimeProvider(Start.AddDays(1))
        );

        (await restarted.ExecuteAsync(restartedStore.GetEngagementRound(roundId)!, CancellationToken.None))
            .Should()
            .Be(EngagementRoundStatus.Completed);
        refrozen.Should().NotContain(MergedCloseSubstep.EvidenceFrozen);
        restartedStore
            .ReadAuditContent(MergedCloseReceiptLedger.RecordId(roundId, MergedCloseSubstep.EvidenceFrozen))
            .Should()
            .Equal(frozenBefore);
    }

    [Fact]
    public async Task Restart_preserves_the_same_round_failure_budget_until_it_parks()
    {
        using var database = new TempSqliteDatabase();
        long roundId;
        using (var firstStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(Start)))
        {
            var round = SeedRound(firstStore);
            roundId = round.Id;
            var firstRunner = Runner(firstStore, new FakeTimeProvider(Start), maxAttempts: 2);

            await firstRunner.RunAsync(
                new EngagementDecision(EngagementDecisionKind.AdmitMergedClose, roundId, "test"),
                null,
                CancellationToken.None
            );

            var retry = firstStore.GetEngagementRound(roundId)!;
            retry.Status.Should().Be(EngagementRoundStatus.RetryPending);
            retry.GovernedFailureCount.Should().Be(1);
        }

        var restartedAt = Start.AddMinutes(5);
        using var restartedStore = new ReviewStore(database.ConnectionString, new FakeTimeProvider(restartedAt));
        var restartedRound = restartedStore.GetEngagementRound(roundId)!;
        var engagement = restartedStore.GetEngagement(restartedRound.PrEngagementId)!;
        var repo = restartedStore.GetRepo(engagement.RepoId)!;
        var watermark = restartedRound.ActivityUpperBound!;
        var snapshot = ProviderEngagementSnapshot.Create(
            PrLifecycleState.Merged,
            restartedRound.HeadSha,
            restartedRound.BaseSha,
            watermark,
            []
        );
        var decision = await new PrEngagementCoordinator(
            restartedStore,
            new FakeTimeProvider(restartedAt)
        ).ObserveAsync(
            engagement.RepoId,
            repo,
            new PullRequestDescriptor
            {
                PrId = engagement.PrId,
                HeadSha = restartedRound.HeadSha,
                BaseSha = restartedRound.BaseSha,
                TriggerWatermark = watermark.StableObjectId,
                LifecycleState = PrLifecycleState.Merged,
            },
            snapshot,
            CancellationToken.None
        );
        var restartedRunner = Runner(restartedStore, new FakeTimeProvider(restartedAt), maxAttempts: 2);

        await restartedRunner.RunAsync(decision, null, CancellationToken.None);

        decision.RoundId.Should().Be(roundId);
        var parked = restartedStore.GetEngagementRound(roundId)!;
        parked.Status.Should().Be(EngagementRoundStatus.Parked);
        parked.GovernedFailureCount.Should().Be(2);
        parked.ParkedAt.Should().Be(restartedAt);
        parked.ParkReason.Should().Be("executor_retry_budget_exhausted");
        restartedStore.ListEngagementRounds(parked.PrEngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Incomplete_checkpoint_replay_keeps_its_original_identity_and_completes()
    {
        using var fixture = new Fixture();
        var substep = MergedCloseSubstep.EvidenceFrozen;
        var content = fixture.FreezeBytes();
        var recordId = MergedCloseReceiptLedger.RecordId(fixture.Round.Id, substep);
        var capturedAt = Start;
        fixture.Store.BeginAuditRecord(
            new AuditSourceRecord(
                recordId,
                fixture.Round.Id,
                $"merged-close:{fixture.Round.Id}",
                fixture.Round.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                substep.ToString(),
                null,
                (long)substep,
                MergedCloseRoundExecutor.SubstepRecordType,
                "system",
                null,
                null,
                Sha256(content),
                content.LongLength,
                Sha256(content),
                content.LongLength,
                AuditSourceCaptureOutcome.Complete,
                null,
                capturedAt,
                null
            )
        );
        var later = new FakeTimeProvider(Start.AddMinutes(5));
        var executor = new MergedCloseRoundExecutor(
            fixture.Store,
            async (round, step, token) =>
                step == MergedCloseSubstep.CandidatesBuilt
                    ? MergedCloseSubstepResult.Retry
                    : await fixture.CompleteForRealAsync(round, step, token),
            later
        );

        var status = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.RetryPending);
        fixture.Store.GetAuditRecord(recordId)!.CompletedAtUtc.Should().Be(capturedAt);
        fixture.Store.ReadAuditContent(recordId).Should().Equal(content);
    }

    [Fact]
    public async Task Throwing_substep_is_not_checkpointed()
    {
        using var fixture = new Fixture();
        var executor = fixture.CreateExecutor(
            (_, _, _) => throw new InvalidOperationException("required output failed")
        );

        var act = () => executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("required output failed");
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType);
    }

    [Fact]
    public async Task Archive_never_runs_after_a_required_substep_requests_retry()
    {
        using var fixture = new Fixture();
        var invoked = new List<MergedCloseSubstep>();
        var executor = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                invoked.Add(step);
                return step == MergedCloseSubstep.KnowledgePromoted
                    ? MergedCloseSubstepResult.Retry
                    : await fixture.CompleteForRealAsync(round, step, token);
            }
        );

        var outcome = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.RetryPending);
        invoked.Should().NotContain(MergedCloseSubstep.Archived);
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.GenerationId == MergedCloseSubstep.Archived.ToString());
    }

    /// <summary>
    /// Spec §10: a pull request that closed without merging produces no outcome report and promotes
    /// nothing. The executor is the last place that can enforce it, so it refuses the round outright
    /// rather than trusting whoever admitted it.
    /// </summary>
    [Fact]
    public async Task Close_without_merge_runs_no_substep_and_leaves_no_checkpoint()
    {
        using var fixture = new Fixture(PrLifecycleState.Closed);
        var invoked = new List<MergedCloseSubstep>();
        var executor = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                invoked.Add(step);
                return await fixture.CompleteForRealAsync(round, step, token);
            }
        );

        var outcome = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.Superseded);
        invoked.Should().BeEmpty();
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType);
        fixture.Store.ListCloseOutcomeItems(fixture.Round.Id).Should().BeEmpty();
        fixture.Store.ListPromotionOutcomes(fixture.Round.Id).Should().BeEmpty();
    }

    /// <summary>
    /// Spec §9.6: finalization requires every required output to exist. A handler that reports success
    /// for every substep without leaving outcome rows, promotion receipts, or report files must not
    /// reach <see cref="MergedCloseSubstep.Archived"/>, which merges and deletes the notes branch.
    /// </summary>
    [Fact]
    public async Task Archive_is_unreachable_while_required_close_outcomes_are_missing()
    {
        using var fixture = new Fixture();
        var invoked = new List<MergedCloseSubstep>();
        var executor = fixture.CreateExecutor(
            (round, step, _) =>
            {
                invoked.Add(step);
                // Real bytes, but no outcome rows, promotion receipts, or report files behind them.
                return Task.FromResult(
                    step == MergedCloseSubstep.EvidenceFrozen
                        ? MergedCloseSubstepResult.Completed(new MergedCloseSubstepReceipt(fixture.FreezeBytes()))
                        : MergedCloseSubstepResult.Completed(
                            new MergedCloseSubstepReceipt(Encoding.UTF8.GetBytes($"claimed:{step}:{round.Id}"))
                        )
                );
            }
        );

        var outcome = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.RetryPending);
        invoked.Should().NotContain(MergedCloseSubstep.Archived);
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.GenerationId == MergedCloseSubstep.Archived.ToString());
        new MergedCloseReceiptLedger(fixture.Store)
            .MissingFinalizationOutcomes(fixture.Round)
            .Should()
            .Contain(MergedCloseFinalizationReasons.OutcomeRowsIncomplete)
            .And.Contain(
                $"{MergedCloseFinalizationReasons.PromotionOutcomeMissing}:{PromotionDestinations.KnowledgeBase}"
            );
    }

    /// <summary>
    /// The promotion pass records a receipt even when it declines, so a missing receipt means the pass
    /// never ran. Archiving over that silence would finalize a close with an unaccounted destination.
    /// </summary>
    [Fact]
    public async Task Archive_is_blocked_when_a_promotion_receipt_is_missing()
    {
        using var fixture = new Fixture();
        var invoked = new List<MergedCloseSubstep>();
        var executor = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                invoked.Add(step);
                return step == MergedCloseSubstep.DeveloperLearningPromoted
                    ? MergedCloseSubstepResult.Completed(
                        new MergedCloseSubstepReceipt(Encoding.UTF8.GetBytes("promoted nothing"))
                    )
                    : await fixture.CompleteForRealAsync(round, step, token);
            }
        );

        var outcome = await executor.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.RetryPending);
        invoked.Should().NotContain(MergedCloseSubstep.Archived);
        new MergedCloseReceiptLedger(fixture.Store)
            .MissingFinalizationOutcomes(fixture.Round)
            .Should()
            .Equal(
                $"{MergedCloseFinalizationReasons.PromotionOutcomeMissing}:{PromotionDestinations.DeveloperLearnings}"
            );
    }

    /// <summary>
    /// Spec §11.4: reports are regenerable from immutable source. A report deleted after it was written
    /// invalidates its receipt, so the writer runs again instead of the round assuming the file is there.
    /// </summary>
    [Fact]
    public async Task Deleted_report_invalidates_its_receipt_so_the_writer_runs_again()
    {
        using var fixture = new Fixture();
        var executor = fixture.CreateExecutor(fixture.CompleteForRealAsync);
        (await executor.ExecuteAsync(fixture.Round, CancellationToken.None))
            .Should()
            .Be(EngagementRoundStatus.Completed);

        File.Delete(fixture.ReportPath);

        var rerun = new List<MergedCloseSubstep>();
        var second = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                rerun.Add(step);
                return await fixture.CompleteForRealAsync(round, step, token);
            }
        );
        var outcome = await second.ExecuteAsync(fixture.Round, CancellationToken.None);

        outcome.Should().Be(EngagementRoundStatus.Completed);
        rerun.Should().Equal(MergedCloseSubstep.ReportsWritten);
        File.Exists(fixture.ReportPath).Should().BeTrue();
    }

    /// <summary>
    /// A checkpoint is trusted for its bytes, not its existence. Content that no longer hashes to the
    /// recorded value has lost the artifact it claims, so it is never counted as a satisfied substep:
    /// the chain halts on it and archiving stays out of reach. The runner turns that fault into governed
    /// failure and, once the budget is spent, visible parked work (spec §11.4).
    /// </summary>
    [Fact]
    public async Task Checkpoint_whose_content_no_longer_matches_its_hash_is_not_treated_as_done()
    {
        using var fixture = new Fixture();
        var first = fixture.CreateExecutor(
            async (round, step, token) =>
                step == MergedCloseSubstep.Classified
                    ? MergedCloseSubstepResult.Retry
                    : await fixture.CompleteForRealAsync(round, step, token)
        );
        (await first.ExecuteAsync(fixture.Round, CancellationToken.None))
            .Should()
            .Be(EngagementRoundStatus.RetryPending);

        fixture.CorruptCheckpointContent(MergedCloseSubstep.CandidatesBuilt);

        var rerun = new List<MergedCloseSubstep>();
        var second = fixture.CreateExecutor(
            async (round, step, token) =>
            {
                rerun.Add(step);
                return await fixture.CompleteForRealAsync(round, step, token);
            }
        );

        var act = () => second.ExecuteAsync(fixture.Round, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should()
            .Contain(MergedCloseSubstep.CandidatesBuilt.ToString());
        rerun.Should().BeEmpty("a checkpoint that cannot be verified halts the chain before any substep runs");
        fixture
            .Store.ListAuditRecordsForRound(fixture.Round.Id)
            .Should()
            .NotContain(record => record.GenerationId == MergedCloseSubstep.Archived.ToString());
    }

    private static EngagementRoundRunner Runner(ReviewStore store, FakeTimeProvider time, int maxAttempts) =>
        new(
            store,
            [new MergedCloseRoundExecutor(store, (_, _, _) => Task.FromResult(MergedCloseSubstepResult.Retry), time)],
            new CodeReviewDaemon.Sample.Configuration.CodeReviewDaemonOptions { MaxDurableRetryAttempts = maxAttempts },
            time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EngagementRoundRunner>.Instance
        );

    /// <summary>A throwaway directory the report substep renders into.</summary>
    private sealed class TempReportRoot : IDisposable
    {
        public TempReportRoot() =>
            Path = Directory
                .CreateDirectory(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"merged-close-{Guid.NewGuid():N}")
                )
                .FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// A stand-in for the close chain Tasks 17-19 will supply: every substep leaves the durable artifact
    /// its receipt claims, so archiving is reachable only because the required outcomes really exist.
    /// </summary>
    private sealed class SubstepChain(ReviewStore store, string reportRoot)
    {
        public string ReportPath => Path.Combine(reportRoot, "outcome.json");

        public byte[] FreezeBytes(EngagementRound round)
        {
            var engagement = store.GetEngagement(round.PrEngagementId)!;
            return MergedCloseEvidenceFreezer.Serialize(
                MergedCloseEvidenceFreezer.Freeze(
                    store,
                    engagement,
                    store.GetRepo(engagement.RepoId)!,
                    round,
                    new MergedCloseProviderEvidence(
                        "merge-commit-sha",
                        "diff-sha",
                        "tree-id",
                        [new MergedCloseDiscussionEntry("c1", "reviewer", Start, "please rename this")],
                        [new MergedClosePromotionDestination(PromotionDestinations.KnowledgeBase, "KB/", "kb-sha")]
                    )
                )
            );
        }

        public async Task<MergedCloseSubstepResult> CompleteAsync(
            EngagementRound round,
            MergedCloseSubstep substep,
            CancellationToken cancellationToken
        )
        {
            switch (substep)
            {
                case MergedCloseSubstep.EvidenceFrozen:
                    return Done(FreezeBytes(round));

                case MergedCloseSubstep.CandidatesBuilt:
                    store.SaveCloseOutcomeItems(round.Id, [Item(round.Id)]);
                    return Done(MergedCloseReceiptPayload.Serialize(new[] { "question:q1" }));

                case MergedCloseSubstep.KnowledgePromoted:
                case MergedCloseSubstep.DeveloperLearningPromoted:
                    var destination =
                        substep == MergedCloseSubstep.KnowledgePromoted
                            ? PromotionDestinations.KnowledgeBase
                            : PromotionDestinations.DeveloperLearnings;
                    store.SavePromotionOutcomes(
                        round.Id,
                        [
                            new PromotionOutcome(
                                "close-obs:test",
                                destination,
                                PromotionDisposition.Declined,
                                null,
                                null,
                                "nothing_confirmed"
                            ),
                        ]
                    );
                    return Done(MergedCloseReceiptPayload.Serialize(new[] { destination }));

                case MergedCloseSubstep.ReportsWritten:
                    var body = Encoding.UTF8.GetBytes($"{{\"closeRoundId\":{round.Id}}}");
                    await File.WriteAllBytesAsync(ReportPath, body, cancellationToken);
                    return Done(
                        MergedCloseReceiptPayload.Serialize(
                            new[] { new MergedCloseReportReceipt(ReportPath, Sha256(body)) }
                        )
                    );

                case MergedCloseSubstep.Classified:
                case MergedCloseSubstep.Verified:
                case MergedCloseSubstep.Archived:
                default:
                    return Done(Encoding.UTF8.GetBytes($"artifact:{substep}:{round.Id}"));
            }
        }

        private static MergedCloseSubstepResult Done(byte[] content) =>
            MergedCloseSubstepResult.Completed(new MergedCloseSubstepReceipt(content));

        private static CloseOutcomeItem Item(long roundId) =>
            new(
                roundId,
                "question:q1",
                CloseCandidateKind.Question,
                "why is this cast safe?",
                null,
                CloseOutcomeLabel.Indeterminate,
                CloseVerificationReasons.NoProposal,
                Start,
                []
            );
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        private readonly TempReportRoot _reports = new();
        private readonly FakeTimeProvider _time = new(Start);
        private readonly SubstepChain _chain;

        public Fixture(PrLifecycleState lifecycle = PrLifecycleState.Merged)
        {
            Store = new ReviewStore(_database.ConnectionString, _time);
            Round = SeedRound(Store, lifecycle);
            _chain = new SubstepChain(Store, _reports.Path);
        }

        public ReviewStore Store { get; }
        public EngagementRound Round { get; }
        public string ReportPath => _chain.ReportPath;

        public MergedCloseRoundExecutor CreateExecutor(MergedCloseSubstepHandler handler) => new(Store, handler, _time);

        public Task<MergedCloseSubstepResult> CompleteForRealAsync(
            EngagementRound round,
            MergedCloseSubstep substep,
            CancellationToken cancellationToken
        ) => _chain.CompleteAsync(round, substep, cancellationToken);

        public byte[] FreezeBytes() => _chain.FreezeBytes(Round);

        /// <summary>
        /// Rewrites a checkpoint's bytes without touching its recorded hash. Content is content-addressed
        /// in <c>audit_blob</c>, so the tamper lands there via the chunk's blob reference, and the length is
        /// preserved so the chunk's own byte accounting stays valid and only the hash stops matching.
        /// </summary>
        public void CorruptCheckpointContent(MergedCloseSubstep substep)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
            connection.Open();
            var recordId = MergedCloseReceiptLedger.RecordId(Round.Id, substep);

            using var read = connection.CreateCommand();
            read.CommandText = """
                SELECT blob.sha256, blob.content
                FROM audit_source_chunk chunk
                JOIN audit_blob blob ON blob.sha256 = chunk.blob_sha256
                WHERE chunk.source_record_id = $id AND chunk.chunk_index = 0;
                """;
            _ = read.Parameters.AddWithValue("$id", recordId);
            using var reader = read.ExecuteReader();
            reader.Read().Should().BeTrue("the substep must already be checkpointed before it is tampered with");
            var sha256 = reader.GetString(0);
            var content = (byte[])reader[1];
            reader.Close();

            content[0] ^= 0xFF;
            using var write = connection.CreateCommand();
            write.CommandText = "UPDATE audit_blob SET content = $content WHERE sha256 = $sha256;";
            _ = write.Parameters.AddWithValue("$content", content);
            _ = write.Parameters.AddWithValue("$sha256", sha256);
            write.ExecuteNonQuery().Should().Be(1);
        }

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
            _reports.Dispose();
        }
    }

    private static EngagementRound SeedRound(ReviewStore store, PrLifecycleState lifecycle = PrLifecycleState.Merged)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", Start, "merge:118");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                lifecycle,
                "merge-head",
                "base-1",
                "merge-head",
                watermark,
                watermark,
                null,
                null,
                null,
                null,
                null,
                Start
            )
        );
        return store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.MergedClose,
                EngagementRoundStatus.Pending,
                "merge-head",
                "base-1",
                watermark,
                watermark,
                0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        )!;
    }

    /// <summary>
    /// Stores one ordinary model-turn audit record — the shape §9.1 freezes as a source record — so a test
    /// can change what a rebuilt manifest would contain without touching close bookkeeping.
    /// </summary>
    private static void StoreEvidenceRecord(ReviewStore store, long roundId, string recordId, DateTimeOffset at)
    {
        var engagementId = store.GetEngagementRound(roundId)!.PrEngagementId;
        var content = Encoding.UTF8.GetBytes(recordId);
        _ = store.StoreAuditRecord(
            new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.ModelTurnAuditRecord(
                recordId,
                new AchieveAi.LmDotnetTools.LmMultiTurn.Audit.MultiTurnAuditScope(
                    engagementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ),
                $"thread:{roundId}",
                roundId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                recordId,
                null,
                0,
                "model_turn",
                "assistant",
                null,
                null,
                content,
                Sha256(content),
                content.LongLength,
                AchieveAi.LmDotnetTools.LmMultiTurn.Audit.AuditCaptureOutcome.Complete,
                null,
                at
            )
        );
    }

    internal static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
