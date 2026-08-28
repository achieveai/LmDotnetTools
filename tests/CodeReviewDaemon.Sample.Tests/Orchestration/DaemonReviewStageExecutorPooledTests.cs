using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 9 — the pooled scoped-writable review flow. When <c>EnableToolAssistedReview</c> +
/// <c>EnableReviewerWrites</c> are on and a store is resolved, <c>ContextReady</c> leases a warm slot and
/// prepares it host-side (branch reuse carries prior notes), the diff comes from the prepared submodule,
/// the review runs with a scoped Write/Edit/Bash tool context, <c>Posted</c> commits ONLY the PR notes dir
/// onto the persistent notes branch (no merge/delete) and returns the slot. Driven entirely against fakes
/// for the pool/preparer/host-git so the wiring is verified without a live gateway.
/// </summary>
public sealed class DaemonReviewStageExecutorPooledTests
{
    private const string StoreUrl = "https://github.com/achieveai/AchieveAiReviews.git";
    private const string Branch = "review/lmdotnettools-118";
    private const string NotesRelPath = "PRs/lmdotnettools-118";
    private const string SubmoduleRelPath = "repos/LmDotnetTools";

    /// <summary>
    /// The stem the review-feedback writer files "octocat" under. Derived rather than typed: what these
    /// tests pin is that the pooled RETRIEVAL path reads the file the writer wrote.
    /// </summary>
    private static readonly string OctocatSlug = ReviewFeedbackAgent.SlugifyAuthor("octocat")!;

    /// <summary>The S2S review host this fixture's deep-links point at (never production's 5050).</summary>
    private const string LmStreamingBaseUrl = "http://localhost:5051";

    /// <summary>The deep-link the Posted stage must append on the S2S path. The hosted loop reports
    /// <c>hosted-{threadId}</c> — standing in for the id LmStreaming MINTS at provision, which is deliberately
    /// NOT the daemon's own <c>review-run-{id}-primary</c> thread id.</summary>
    private static string S2SDeepLink(ReviewRun run) =>
        $"{LmStreamingBaseUrl}/?threadId=hosted-{DaemonReviewStageExecutor.ThreadId(run, run.VariantId)}&focus=1";

    [Fact]
    public async Task ContextReady_leases_a_slot_prepares_it_and_diffs_the_prepared_target()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1);
        fixture.Pool.ReturnCount.Should().Be(0, "the slot is held for the review + commit-notes + terminal return");
        fixture.Preparer.PrepareCount.Should().Be(1);
        fixture.Preparer.LastSubmoduleRelPath.Should().Be(SubmoduleRelPath);
        fixture.Preparer.LastBranch.Should().Be(Branch);
        fixture.Preparer.LastNotesRelPath.Should().Be(NotesRelPath);
        fixture.Preparer.LastDefaultBranch.Should().Be("main");

        // The diff is taken through the run-bound SDK session, never the host or boot runner.
        fixture
            .Provisioner.SdkRunner.Commands.Select(Join)
            .Should()
            .Contain(a => a.Contains("/workspace/store/repos/LmDotnetTools") && a.Contains("diff"));
        fixture.HostRunner.Commands.Should().BeEmpty("host git is reserved for the post-review commit gate");
        fixture.BootRunner.Commands.Should().BeEmpty("the pooled path never touches the boot-lifetime runner");

        // The artifact records the CONTAINER paths the agent's tools address (slot mounted at /workspace).
        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var payload = JsonDocument.Parse(artifact.Payload).RootElement;
        payload.GetProperty("CheckoutRoot").GetString().Should().Be("/workspace/store/repos/LmDotnetTools");
        payload.GetProperty("StoreRoot").GetString().Should().Be("/workspace/store");
        payload.GetProperty("Diff").GetString().Should().Contain("Foo.cs");
    }

    [Fact]
    public async Task ContextReady_falls_back_to_the_per_run_checkout_when_the_repo_is_not_a_store_submodule()
    {
        using var fixture = Fixture.Create();
        // The store declares a DIFFERENT submodule, so the reviewed repo is not in it: the pooled path
        // declines (returns the slot) and the executor uses the existing per-run/diff-only checkout.
        fixture.HostFileSystem.Files.Clear();
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/.gitmodules",
            "[submodule \"other\"]\n\tpath = repos/other\n\turl = https://github.com/achieveai/other.git\n"
        );
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1);
        fixture
            .Pool.ReturnCount.Should()
            .Be(1, "a declined lease is returned immediately so it can't leak pool capacity");
        fixture.Preparer.PrepareCount.Should().Be(0, "the reviewed repo is not a store submodule, so no prepare runs");
        // The stage still completed via the fallback checkout — a context artifact was persisted.
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ContextArtifactKind);
    }

    [Fact]
    public async Task ContextReady_reclones_and_retries_prepare_once_when_the_slot_is_corrupt()
    {
        using var fixture = Fixture.Create();
        // The warm store is corrupt: the first prepare reports it, the executor re-clones and retries once.
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("stale lock survived"));
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        fixture.Preparer.RecloneCount.Should().Be(1, "the session-bound preparer re-clones before retry");
        fixture.Preparer.PrepareCount.Should().Be(2, "prepare is retried exactly once after the re-clone");
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .ContainSingle(
                a => a.ArtifactKind == DaemonReviewStageExecutor.ContextArtifactKind,
                "the retried prepare succeeded, so the stage completed with a context artifact"
            );
    }

    [Fact]
    public async Task ContextReady_surfaces_and_returns_the_slot_when_prepare_still_fails_after_reclone()
    {
        using var fixture = Fixture.Create();
        // Corrupt twice: re-clone + retry does not help, so the failure surfaces (the retry governor bounds it)
        // and the slot must be returned so it cannot leak pool capacity.
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("corrupt 1"));
        fixture.Preparer.ThrowThenSucceed.Enqueue(new SlotCorruptException("corrupt 2"));
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        await act.Should().ThrowAsync<SlotCorruptException>();
        fixture.Preparer.RecloneCount.Should().Be(1, "the session-bound preparer attempts one re-clone");
        fixture.Preparer.PrepareCount.Should().Be(2, "prepare is attempted once, then once more after the re-clone");
        fixture.Pool.ReturnCount.Should().Be(1, "the failed lease is returned so it cannot leak pool capacity");
    }

    [Fact]
    public async Task ContextReady_retires_the_slot_instead_of_returning_it_when_preparation_refuses_a_host_path()
    {
        using var fixture = Fixture.Create();
        // The refusal above is about the ATTEMPT, so the slot goes back. This one is about the ADDRESS: an entry
        // under the slot could not be established as contained, and it will still be uncontained on the next
        // lease. The lease guard cannot catch it — it checks the three slot paths and the entry is a descendant
        // of one — so returning the index to a free list that is a STACK hands it to the very next run, which
        // refuses again, forever. Retiring costs one directory name; the permit is released either way.
        fixture.Preparer.ThrowThenSucceed.Enqueue(
            new SlotAddressUnusableException("Refusing to wipe host directory 'store': '.git/objects' — unreadable.")
        );
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        await act.Should().ThrowAsync<SlotAddressUnusableException>();
        fixture
            .Preparer.RecloneCount.Should()
            .Be(
                0,
                "the re-clone starts by wiping the store, which is the walk that just refused — escalating there is "
                    + "the redirected write, so the recovery ladder must filter by TYPE and not by 'prepare failed'"
            );
        fixture.Preparer.PrepareCount.Should().Be(1, "and with no re-clone there is nothing to retry against");
        fixture.Pool.RetireCount.Should().Be(1);
        fixture.Pool.Retired.Should().ContainSingle().Which.Should().Be(fixture.Pool.Leased.Single());
        fixture
            .Pool.ReturnCount.Should()
            .Be(
                0,
                "returning is what recycles the address — the test above pins that an ordinary prepare failure DOES "
                    + "return, so this zero is a routing decision and not an unreached path"
            );
    }

    /// <summary>
    /// The one merge-base outcome licensed to become a stated fact about someone's pull request. Preparation
    /// walked BOTH histories to real root commits and established that the commits share no ancestor, so the
    /// diff was always going to fail: throwing here burns the whole retry budget re-deriving a permanent fact
    /// and leaves the PR with no verdict at all. The reason is recorded on the artifact instead.
    /// </summary>
    [Fact]
    public async Task ContextReady_records_an_uncomparable_reason_instead_of_failing_when_the_histories_are_unrelated()
    {
        using var fixture = Fixture.CreateS2S();
        fixture.Preparer.MergeBase = MergeBaseOutcome.UnrelatedHistories;
        _ = fixture.HostRunner.OnArgvContainsFirst(
            "diff",
            new SandboxCommandResult(
                128,
                string.Empty,
                "fatal: refusing to merge unrelated histories\nhint: see git-merge(1)"
            )
        );
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var artifact = fixture.Store.GetArtifacts(run.Id).Should().ContainSingle().Subject;
        var payload = JsonDocument.Parse(artifact.Payload).RootElement;
        var reason = payload.GetProperty("UncomparableReason").GetString();
        reason
            .Should()
            .Contain(
                "share no common ancestor",
                "the stage's whole job on this outcome is to leave the record saying WHY, in the author's terms"
            );
        reason
            .Should()
            .Contain(
                "refusing to merge unrelated histories",
                "quoted from git's own first stderr line — the claim has to carry the evidence it rests on"
            );
        reason
            .Should()
            .NotContain(
                "hint: see git-merge(1)",
                "only the first line of a git stderr is the failure; the advice paragraphs after it are noise in "
                    + "a sentence a person reads"
            );
        artifact
            .ArtifactSchemaVersion.Should()
            .Be(
                DaemonReviewStageExecutor.ContextArtifactSchemaVersion,
                "a row that can carry a reason is a v2 row — a v1 row's silence means 'not measured', because "
                    + "before v2 this run wrote no artifact at all"
            );
    }

    /// <summary>
    /// The negative that the whole three-state probe exists for, stated at the level the author reads. A
    /// merge-base probe that was killed or failed established NOTHING about anyone's branch, so it may not
    /// reach the one outcome whose verdict tells a pull-request author to re-target or rebase.
    /// <para>
    /// Asserted on the persisted text rather than only on the exception type: if a later change gives
    /// Indeterminate a verdict of its own, that verdict has to be checked here rather than silently
    /// inheriting the wording that blames the branch.
    /// </para>
    /// </summary>
    /// <summary>
    /// The record is not the deliverable — the brief is. Recording `UncomparableReason` and then not reading
    /// it is worse than not recording it: ContextReady writes `ChangedPaths` as `""` on this path, which is
    /// indistinguishable from the older-artifact shape `BuildReviewInput`'s degraded arm exists for, so the
    /// run would log "no changed-path listing … falling back to the inlined diff (0 chars)" — naming a cause
    /// that is false and a fallback onto a diff that does not exist — and then fan a full review out against
    /// nothing, with no line anywhere telling the reviewer the range is undiffable.
    /// <para>
    /// Both halves are asserted on the SAME run, because either alone passes on the broken code: the brief
    /// could carry the reason while the false warning still fires, and the warning could be silenced while
    /// the brief stays empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reviewed_tells_the_reviewer_the_commits_are_uncomparable_instead_of_a_false_diff_fallback()
    {
        using var fixture = Fixture.CreateS2S();
        fixture.Preparer.MergeBase = MergeBaseOutcome.UnrelatedHistories;
        _ = fixture.HostRunner.OnArgvContainsFirst(
            "diff",
            new SandboxCommandResult(
                128,
                string.Empty,
                "fatal: refusing to merge unrelated histories\nhint: see git-merge(1)"
            )
        );
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var brief = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        brief
            .Should()
            .Contain(
                "share no common ancestor",
                "the reason ContextReady established is the one thing about this range the reviewer cannot work "
                    + "out for itself"
            );
        brief
            .Should()
            .Contain(
                "Do NOT substitute another range",
                "an agent with tools and no diff picks a range of its own — that is the failure this brief exists "
                    + "to pre-empt, not a hypothetical one"
            );

        fixture
            .Logs.Capturing.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .NotContain(
                m => m.Contains("falling back to the inlined diff", StringComparison.Ordinal),
                "that line names a cause that is false here and promises a fallback onto a diff that does not "
                    + "exist; the uncomparable branch has to be taken BEFORE the degraded arm, not alongside it"
            );
    }

    [Fact]
    public async Task ContextReady_never_tells_an_author_to_rebase_when_our_own_probe_was_the_thing_that_failed()
    {
        using var fixture = Fixture.CreateS2S();
        fixture.Preparer.MergeBase = MergeBaseOutcome.Indeterminate;
        _ = fixture.HostRunner.OnArgvContainsFirst(
            "diff",
            new SandboxCommandResult(
                124,
                string.Empty,
                "git diff produced no output for 300s (idle timeout) and was killed"
            )
        );
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        _ = await act.Should()
            .ThrowAsync<InvalidOperationException>(
                "nothing was learned about these commits, so the stage stays a failure and the run retries — "
                    + "which is precisely what would have fixed it"
            );
        var written = string.Join('\n', fixture.Store.GetArtifacts(run.Id).Select(a => a.Payload));
        written
            .Should()
            .NotContain(
                "share no common ancestor",
                "that sentence is a statement about the author's branch, and nothing about their branch was "
                    + "established — our own git command was killed"
            );
    }

    /// <summary>
    /// The asymmetry, enumerated. Every outcome except <c>UnrelatedHistories</c> is either recoverable by
    /// widening a bound this daemon chose, or a step that broke before it could learn anything — so each one
    /// keeps throwing. Converting any of them into "these commits cannot be compared" takes our own
    /// configuration limit or a network blip and hands it to a PR author as a fact about their branch.
    /// </summary>
    [Fact]
    public Task ContextReady_still_fails_when_the_diff_fails_and_a_merge_base_was_found() =>
        AssertDiffFailureStillFails(MergeBaseOutcome.Resolved);

    [Fact]
    public Task ContextReady_still_fails_when_the_diff_fails_and_the_depth_ceiling_was_reached() =>
        AssertDiffFailureStillFails(MergeBaseOutcome.DepthCeilingReached);

    [Fact]
    public Task ContextReady_still_fails_when_the_diff_fails_and_a_deepening_fetch_broke() =>
        AssertDiffFailureStillFails(MergeBaseOutcome.DeepenFailed);

    [Fact]
    public Task ContextReady_still_fails_when_the_diff_fails_and_a_merge_base_probe_never_answered() =>
        AssertDiffFailureStillFails(MergeBaseOutcome.Indeterminate);

    private static async Task AssertDiffFailureStillFails(MergeBaseOutcome outcome)
    {
        using var fixture = Fixture.CreateS2S();
        fixture.Preparer.MergeBase = outcome;
        _ = fixture.HostRunner.OnArgvContainsFirst(
            "diff",
            new SandboxCommandResult(128, string.Empty, "fatal: bad object base-sha")
        );
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .BeEmpty("a stage that failed leaves no partial record behind for a resumed run to mistake for a capture");
    }

    [Fact]
    public async Task Reviewed_builds_a_read_only_tool_context()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The context carries the read-only allow-list as data. Write-scoping is NOT carried here, and no mount
        // takes its place: writes outside the notes dir are ineffective rather than blocked — the commit gate
        // stages only the PR's notes dir and the next lease's clean-on-entry erases the rest — so this
        // context no longer has any
        // write-scope fields to assert (issue #490).
        var toolContext = fixture.Factory.ToolContexts.Where(t => t is not null).Should().ContainSingle().Subject!;
        toolContext.ReadOnlyToolAllowList.Should().BeEquivalentTo(["Read", "Grep", "Glob", "Skill"]);
    }

    [Fact]
    public async Task Reviewed_prepends_the_knowledge_base_toc_read_from_the_leased_slots_host_store()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The pooled path must read KnowledgeBase/_toc.md HOST-side from the LEASED SLOT's store checkout —
        // via _slotWorkspace.HostFileSystem + lease.Prepared.StoreRoot — not the boot-lifetime sandbox
        // session (fixture.BootFileSystem), which the gateway never registers for a pooled run and 404s
        // ("Session not found"). Seed the ToC on the HOST file system at the slot's store root, mirroring
        // what a real KnowledgeExtractionCommitter run would have already committed there.
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [KB-ENTRY-XYZ](system/kb-entry-xyz.md)\n"
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;

        // The heading MUST be the canonical one the review prompt teaches, because the prompt also teaches
        // that the absence of that block means there is no Knowledge Base at all. A fallback rendered under
        // its own heading is therefore invisible: the agent is told, in the same breath, not to go looking.
        text.Should().Contain("## Prior knowledge (Knowledge Base)", "the ToC is prepended as a labelled block");
        text.Should().Contain("KB-ENTRY-XYZ", "the seeded ToC entry is surfaced to the pooled reviewer");
        text.Should()
            .Contain(
                "/workspace/store/KnowledgeBase/_toc.md",
                "the fallback must still hand over an exact absolute path, not a bare file name"
            );
    }

    [Fact]
    public async Task Reviewed_renders_container_rooted_knowledge_paths_when_the_leased_store_is_a_host_path()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        // The two roots diverge on the pooled S2S path and MUST NOT be conflated. The daemon reads the KB
        // host-side out of the leased slot (lease.Prepared.StoreRoot = the slot's HOST store dir), but the
        // reviewer is a hosted agent for which that slot is mounted at /workspace — so every path rendered
        // INTO its input has to be container-rooted, exactly like the container roots the context artifact
        // advertises. Rendering the host path hands the agent a Windows/host path it cannot open, which
        // silently defeats the whole feature on the supported path.
        fixture.HostFileSystem.Seed(
            "/pool/review-slot-0/store/KnowledgeBase/_index.jsonl",
            """{"file":"system/null-guard.md","title":"Null-guard boundaries","tags":["null"],"scope":"system","sourcePrs":[],"updated":"2026-07-05"}"""
                + "\n"
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should()
            .Contain(
                "/workspace/store/KnowledgeBase/system/null-guard.md",
                "the entry was READ from the host slot but must be RENDERED at the root the agent sees"
            );
        text.Should()
            .NotContain(
                "/pool/review-slot-0",
                "a host path is unopenable inside the review container, so it must never reach the agent's input"
            );
    }

    [Fact]
    public async Task Reviewed_prepends_the_authors_feedback_record_read_from_the_leased_slots_host_store()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun(prAuthor: "octocat");

        // Same guarantee as the prior-knowledge ToC above, on the payload that ships beside it. The record
        // lives in the LEASED SLOT's store checkout and must be read HOST-side via _slotWorkspace
        // .HostFileSystem + lease.Prepared.StoreRoot; the boot-lifetime sandbox session is never registered
        // for a pooled run and 404s. Reading through the wrong file system does not throw here — it reports
        // "absent", which is indistinguishable from an author who has no record yet, so the feature would
        // simply never fire on the supported path.
        fixture.HostFileSystem.Seed(
            $"/pool/slot-0/store/KnowledgeBase/developers/{OctocatSlug}.reviewfeedbacks.md",
            "---\ndeveloper: octocat\n---\n\n## Patterns\n\n- FEEDBACK-PATTERN-XYZ\n"
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should()
            .Contain("## Recurring feedback for this PR's author", "the record is prepended as a labelled block");
        text.Should().Contain("FEEDBACK-PATTERN-XYZ", "the seeded record body is surfaced to the pooled reviewer");
        text.Should()
            .Contain(
                $"/workspace/store/KnowledgeBase/developers/{OctocatSlug}.reviewfeedbacks.md",
                "the heading hands over an exact absolute path, not a bare file name"
            );
    }

    [Fact]
    public async Task Reviewed_renders_a_container_rooted_feedback_path_when_the_leased_store_is_a_host_path()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun(prAuthor: "octocat");

        // The read/render split, mirrored onto the feedback record. This block tells the agent to open the
        // path with the Read tool AND to copy it into every sub-agent's brief, so a host path here is worse
        // than a missing one: it propagates an unopenable path to every child that was dispatched to look
        // for exactly these mistakes. Read host-side out of the leased slot, render at the mounted root.
        fixture.HostFileSystem.Seed(
            $"/pool/review-slot-0/store/KnowledgeBase/developers/{OctocatSlug}.reviewfeedbacks.md",
            "---\ndeveloper: octocat\n---\n\n## Patterns\n\n- Leaves `ConfigureAwait(false)` off library awaits.\n"
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var block = text[text.IndexOf("## Recurring feedback", StringComparison.Ordinal)..];
        block
            .Should()
            .Contain(
                $"/workspace/store/KnowledgeBase/developers/{OctocatSlug}.reviewfeedbacks.md",
                "the record was READ from the host slot but must be RENDERED at the root the agent sees"
            );
        block
            .Should()
            .NotContain(
                "/pool/review-slot-0",
                "a host path is unopenable inside the review container, and this block tells the agent to forward it to sub-agents"
            );
    }

    [Fact]
    public async Task Reviewed_points_the_reviewer_at_git_instead_of_inlining_the_patch_and_the_file_tree()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;

        // The two payloads that used to dominate the brief. On a real run (226) they were 117k and 15.6k chars
        // of a 173,567-char input, and the reviewer holds a checkout of the head that answers both.
        text.Should()
            .NotContain(
                "Tracked files in the reviewed repository",
                "the reviewer has the checkout and can Glob/ls-files it; listing every tracked file is dead weight"
            );
        text.Should().NotContain("\n\nDiff:\n", "the patch is read from git now, not copied into the brief");

        // What replaces them has to leave the reviewer able to get there on its own: the range, the root, and
        // the changed-file listing (which the KB ranking already computes, so this costs nothing new).
        text.Should().Contain("Files changed (", "the reviewer still needs to know the blast radius up front");
        text.Should()
            .Contain(
                $"diff {run.BaseSha}...{run.HeadSha}",
                "the fetch instruction must carry the range, which is the one thing the reviewer cannot derive"
            );
        text.Should().Contain("git -C ", "the instruction must be runnable as written, not assembled by the model");
        text.Should()
            .NotContain(
                "/pool/slot-0",
                "the brief now tells the reviewer to run git at this root, so a HOST path here would be a command "
                    + "that cannot run inside the review container (cf. the sub-agent block above)"
            );
        text.Should()
            .Contain(
                "UNTRUSTED DATA",
                "the injection warning the inlined diff/guidance used to carry must survive their removal - the "
                    + "reviewer is now reading that same attacker-controlled content through its own tools"
            );
    }

    /// <summary>
    /// The degrade path. A context artifact carries no changed-path listing either because
    /// <c>git diff --name-only</c> failed (pinned here, since that is the trigger a live run can hit) or because
    /// the artifact predates the field and is being resumed now — run 220 in the achieveai daemon's store is
    /// exactly that second shape, a null listing beside a 44,649-char diff, while 221-224 carry the listing.
    /// Either way the reviewer must still be told what the PR touched, so the brief falls back to inlining the
    /// patch rather than shipping a range with no blast radius attached.
    /// </summary>
    [Fact]
    public async Task Reviewed_falls_back_to_the_inlined_patch_when_there_is_no_changed_path_listing()
    {
        using var fixture = Fixture.Create();
        fixture.Provisioner.NameOnlyResult = new SandboxCommandResult(128, string.Empty, "fatal: bad object");
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;

        text.Should()
            .Contain(
                "diff --git a/Foo.cs",
                "with no listing the patch is the only record of what the PR touched, so it is inlined rather than "
                    + "leaving the reviewer to review blind"
            );
        text.Should()
            .NotContain(
                "Files changed (",
                "there is no listing to report, and an empty one would read as 'this PR changed nothing'"
            );
    }

    [Fact]
    public async Task Reviewed_points_the_reviewer_at_the_repos_root_guidance_instead_of_quoting_it()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The reviewed repo's own CLAUDE.md/AGENTS.md live in the LEASED SLOT's target checkout
        // (lease.Prepared.TargetDir = <store>/repos/LmDotnetTools) and must be PROBED host-side via
        // _slotWorkspace.HostFileSystem — the same host filesystem the KB / prior-notes reads use, NOT the
        // boot-lifetime sandbox session (which the gateway never registers for a pooled run).
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/repos/LmDotnetTools/CLAUDE.md",
            "# LmDotnetTools\nUse CSharpier. REPO-GUIDANCE-MARKER."
        );
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/repos/LmDotnetTools/AGENTS.md",
            "Agents must read AGENTS-MARKER before reviewing."
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("Repository guidance", "the reviewer still has to be told the files are there");
        text.Should()
            .Contain(
                "/workspace/store/repos/LmDotnetTools/CLAUDE.md",
                "the pointer is only useful at the root the AGENT's tools resolve; the host path the daemon "
                    + "probed through (/pool/slot-0/...) does not exist inside the review container"
            );
        text.Should().Contain("/workspace/store/repos/LmDotnetTools/AGENTS.md", "both files are named");
        text.Should()
            .NotContain(
                "/pool/slot-0",
                "rendering the daemon's own disk path fails silently - the block reads fine and every Read of it "
                    + "404s in the container"
            );
        text.Should()
            .NotContain(
                "REPO-GUIDANCE-MARKER",
                "the file is pointed at, not quoted - on run 226 this content was ~24,500 chars of a 173,567-char "
                    + "brief, for a file the reviewer holds a checkout of"
            );
        text.Should().NotContain("AGENTS-MARKER", "same for AGENTS.md");
        text.Should()
            .Contain(
                "prompt injection",
                "the warning has to travel with the pointer: the reviewer now reads that attacker-controlled text "
                    + "through its own tools, where nothing else marks it as untrusted"
            );
    }

    [Fact]
    public async Task Reviewed_skips_the_repo_guidance_block_when_neither_file_exists()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // No CLAUDE.md / AGENTS.md seeded in the checkout — the block must be silently omitted (design §6:
        // the enrichment must never fail or pollute the review), leaving the review input clean.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("Repository guidance", "an absent CLAUDE.md/AGENTS.md must not add an empty block");
    }

    [Fact]
    public async Task Reviewed_tells_the_reviewer_the_knowledge_base_was_unread_when_every_listing_is_refused()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // BOTH listings over the ceiling: nothing about the store reaches the reviewer. The failure this
        // pins is not the missing knowledge — that part is unavoidable once a file is refused — it is what
        // the SILENCE would say. The review prompt teaches that the absence of the "## Prior knowledge"
        // heading means this repository has no Knowledge Base, so degrading a refusal to "no prior
        // knowledge" does not withhold a fact, it asserts a false one to the only party that acts on it.
        var oversize = new string('x', (int)SandboxReadLimits.KnowledgeListingBytes + 1);
        fixture.HostFileSystem.Seed("/pool/slot-0/store/KnowledgeBase/_index.jsonl", oversize);
        fixture.HostFileSystem.Seed("/pool/slot-0/store/KnowledgeBase/_toc.md", oversize);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should()
            .Contain(
                "## Prior knowledge (Knowledge Base)",
                "the refusal has to arrive under the one heading the prompt teaches, or it is invisible"
            );
        text.Should()
            .Contain(
                "/workspace/store/KnowledgeBase/_index.jsonl",
                "the reviewer is told exactly which listing was refused, at the root it can resolve"
            );
        text.Should().Contain("/workspace/store/KnowledgeBase/_toc.md", "both routes were refused, so both are named");
        text.Should()
            .NotContain(
                "xxxxxxxxxx",
                "a refused file is never rendered in part — the point of refusing is that no prefix is safe"
            );
    }

    [Fact]
    public async Task Reviewed_does_not_announce_a_refusal_when_the_fallback_listing_was_read()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Only the INDEX is refused; _toc.md is fine and carries the entries. The reviewer has its prior
        // knowledge, so it must not also be told the store is unread — an alarm raised on every run where
        // ranking degraded to the fallback is an alarm nobody reads on the run that matters.
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/KnowledgeBase/_index.jsonl",
            new string('x', (int)SandboxReadLimits.KnowledgeListingBytes + 1)
        );
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/KnowledgeBase/_toc.md",
            "# Knowledge Base\n\n## system\n- [KB-ENTRY-XYZ](system/kb-entry-xyz.md)\n"
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("KB-ENTRY-XYZ", "the readable fallback listing still reaches the reviewer");
        text.Should()
            .NotContain(
                "could be loaded for this review",
                "nothing was lost to the reviewer, so nothing about a refusal belongs in its input"
            );
    }

    [Fact]
    public async Task Reviewed_still_points_at_repo_guidance_that_is_too_large_for_the_daemon_to_read()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // TooLarge is a POSITIVE existence signal, not a failure. It used to matter a great deal — the file
        // was announced and never seen, because the daemon's ingest ceiling also decided what the reviewer
        // could read. Now that nothing is quoted, that ceiling is the daemon's problem alone: a refused file
        // is named exactly like a read one, and the reviewer opens it with its own budget.
        fixture.HostFileSystem.Seed(
            "/pool/slot-0/store/repos/LmDotnetTools/CLAUDE.md",
            "REPO-GUIDANCE-MARKER" + new string('x', (int)SandboxReadLimits.RepositoryFileBytes)
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("Repository guidance", "the block is rendered - the file exists");
        text.Should()
            .Contain(
                "/workspace/store/repos/LmDotnetTools/CLAUDE.md",
                "an oversize file is pointed at like any other; skipping it silently would have the reviewer "
                    + "fault a PR for conventions it was never shown"
            );
        text.Should()
            .NotContain(
                "REPO-GUIDANCE-MARKER",
                "no prefix of a refused file is quoted - and none of any other file either, now"
            );
    }

    [Fact]
    public async Task Reviewed_prepends_existing_pr_comments_so_the_reviewer_posts_only_new_findings()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Simulate a PR that already has prior review comments — the daemon fetches them HOST-side (via the
        // provider's IReviewCommentPublisher) and folds them into the review INPUT so the reviewer adds only
        // genuinely NEW findings instead of re-posting a full review every run (the "45 reviews on one PR" bug).
        // The block must surface each comment's ACTIVE/RESOLVED status and its author (from ANY author — other
        // bots and humans), and instruct the reviewer to answer questions directed at it.
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "42",
                "Must — null deref EXISTING-FINDING",
                "revobot",
                IsActive: true
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment("src/Bar.cs", "7", "Should — extract EXISTING-RESOLVED", "alice", IsActive: false)
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().Contain("Already posted on this PR", "existing comments are prepended as a labelled dedup block");
        text.Should().Contain("from ALL authors", "the reviewer must consider comments from other bots and humans too");
        text.Should().Contain("Comments during past reviews", "the block is split into past vs new");
        text.Should().Contain("New comments since your last review", "…so new discussion is called out for focus");
        text.Should().Contain("src/Foo.cs:42 [status: active]", "an open thread shows its location + status hint");
        text.Should().Contain("(revobot", "each comment is attributed to its author");
        text.Should().Contain("src/Bar.cs:7 [status: resolved]", "a resolved thread is tagged resolved");
        text.Should().Contain("(alice", "a human author is attributed too");
        text.Should().Contain("EXISTING-FINDING");
        text.Should().Contain("EXISTING-RESOLVED");
        text.Should()
            .Contain(
                "UNTRUSTED DATA",
                "existing comment bodies must be framed as untrusted quoted data (prompt-injection defense)"
            );
        text.Should()
            .Contain("«Must — null deref EXISTING-FINDING»", "each untrusted body is wrapped in guillemet delimiters");
        text.Should().Contain("ANSWER it as an in-thread reply", "a question directed at the bot must be answered");
        text.Should()
            .Contain(
                "No new findings since the last review",
                "the reviewer is told to post nothing when there is nothing new"
            );
    }

    [Fact]
    public async Task Reviewed_splits_existing_comments_into_past_reviews_and_new_since_last_review()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The cutoff is the bot's most recent finding: a "[…bot…]"-prefixed comment (here "[Revobot] …"). A human
        // comment posted AFTER it belongs under "New comments since your last review"; the bot's own older finding
        // belongs under "Comments during past reviews". Different thread ids keep them as separate threads.
        var botFindingTime = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var humanReplyTime = DateTimeOffset.Parse("2026-07-21T09:00:00Z");
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "[Revobot] PAST-BOT-FINDING",
                "revobot",
                IsActive: true,
                PublishedAt: botFindingTime,
                ThreadId: "th-bot"
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "20",
                "Alice asks: NEW-HUMAN-QUESTION for the bot?",
                "alice",
                IsActive: true,
                PublishedAt: humanReplyTime,
                ThreadId: "th-human"
            )
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var pastIdx = text.IndexOf("Comments during past reviews", StringComparison.Ordinal);
        var newIdx = text.IndexOf("New comments since your last review", StringComparison.Ordinal);
        var pastFindingIdx = text.IndexOf("PAST-BOT-FINDING", StringComparison.Ordinal);
        var newQuestionIdx = text.IndexOf("NEW-HUMAN-QUESTION", StringComparison.Ordinal);

        pastIdx.Should().BeGreaterThan(0);
        newIdx.Should().BeGreaterThan(pastIdx, "the new-comments section comes after the past-reviews section");
        pastFindingIdx.Should().BeInRange(pastIdx, newIdx, "the bot's older finding sits under past reviews");
        newQuestionIdx.Should().BeGreaterThan(newIdx, "the later human question sits under new-since-last-review");
    }

    [Fact]
    public async Task Reviewed_renders_each_thread_oldest_first_even_when_fetched_newest_first()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // GitHub inline comments are fetched NEWEST-first (so the page cap keeps the most recent activity). Within a
        // single thread that reverses the conversation — the reviewer is told to read root-finding → replies to judge
        // resolution, so each thread must render OLDEST-first regardless of fetch order. Seed reply-before-root to
        // mirror the descending fetch and assert the root finding renders before its later reply.
        var rootTime = DateTimeOffset.Parse("2026-07-20T10:00:00Z");
        var replyTime = DateTimeOffset.Parse("2026-07-22T15:00:00Z");
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "REPLY-fixed-in-abc123",
                "alice",
                IsActive: true,
                PublishedAt: replyTime,
                ThreadId: "th-1"
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "ROOT-null-deref-finding",
                "revobot",
                IsActive: true,
                PublishedAt: rootTime,
                ThreadId: "th-1"
            )
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var rootIdx = text.IndexOf("ROOT-null-deref-finding", StringComparison.Ordinal);
        var replyIdx = text.IndexOf("REPLY-fixed-in-abc123", StringComparison.Ordinal);
        rootIdx.Should().BeGreaterThan(0, "the root finding must be rendered");
        replyIdx
            .Should()
            .BeGreaterThan(
                rootIdx,
                "within a thread the root finding renders before its later reply so the reviewer reads the conversation in order"
            );
    }

    [Fact]
    public async Task Reviewed_skips_the_existing_comments_block_when_the_pr_has_none()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // No prior comments seeded → the dedup block must be omitted (a first review has nothing to dedup against).
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        text.Should().NotContain("Already posted on this PR");
    }

    // ── the already-posted block's shared size budget (#225 items 3+4) ──
    //
    // The block used to apply its comment cap to EACH rendered section, so the number a reader takes as the bound
    // was quietly worth double, and a comment count was never a size bound anyway: bodies are written by anyone
    // who can comment on the PR. These pin one budget — characters AND comments — drawn down across both
    // sections, with the "new since your last review" section CLAIMING IT FIRST, because that is the one the
    // guidance tells the reviewer to look hardest at: it carries questions it is REQUIRED to answer.
    //
    // The numbers below mirror the executor's private constants: 40,000 chars / 400 comments for the block, half
    // of each being the share the past-reviews section may never take.
    private const int BlockCharBudget = 40_000;
    private const int BlockCommentBudget = 400;
    private const int NewSectionReservation = BlockCharBudget / 2;

    /// <summary>Splits the rendered dedup block into its two sections so a test can size them independently.</summary>
    private static (string Past, string New) DedupSections(string text)
    {
        var pastIdx = text.IndexOf("### Comments during past reviews", StringComparison.Ordinal);
        var newIdx = text.IndexOf("### New comments since your last review", StringComparison.Ordinal);
        pastIdx.Should().BeGreaterThan(0, "the past-reviews section must be rendered for this test to mean anything");
        newIdx.Should().BeGreaterThan(pastIdx, "the new-comments section follows it");

        // Every rendered line inside a section ends in a SINGLE newline (the renderer trims trailing ones), so the
        // first blank line after the new-comments heading is where the block ends and the review input begins.
        var endIdx = text.IndexOf("\n\n", newIdx, StringComparison.Ordinal);
        endIdx.Should().BeGreaterThan(newIdx);
        return (text[pastIdx..newIdx], text[newIdx..endIdx]);
    }

    private static int RenderedCommentCount(string text) =>
        text.Split('\n').Count(l => l.StartsWith("    - (", StringComparison.Ordinal));

    /// <summary>Seeds <paramref name="count"/> comments of roughly <paramref name="bodyChars"/> characters each,
    /// all before the bot cutoff (past) or all after it (new), each in a thread of its own.</summary>
    private static void SeedBulkComments(
        Fixture fixture,
        string marker,
        int count,
        int bodyChars,
        bool isNew,
        string? threadId = null
    )
    {
        var cutoff = DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture);
        for (var i = 0; i < count; i++)
        {
            fixture.Publisher.ExistingComments.Add(
                new ExistingReviewComment(
                    "src/Foo.cs",
                    i.ToString(CultureInfo.InvariantCulture),
                    $"{marker}-{i:D3}-" + new string('x', bodyChars),
                    "alice",
                    IsActive: true,
                    // Ascending in i either side of the cutoff, so index order IS conversation order — a thread renders
                    // oldest-first, and a test that names PAST-000 must be naming the comment rendered first.
                    PublishedAt: isNew ? cutoff.AddHours(i + 1) : cutoff.AddHours(i - count),
                    ThreadId: threadId ?? $"{marker}-{i:D3}"
                )
            );
        }
    }

    // ── #225 item 1: the cutoff comes from a durable stamp, not only from comment bodies ──────────

    [Fact]
    public async Task Reviewed_takes_the_cutoff_from_the_posted_stamp_when_no_bot_prefix_survives()
    {
        // The defect, exactly. Nothing on this PR carries a "[…bot]" body prefix — a batched review or a
        // summary posted without one is enough — so the body scan finds no cutoff. Before the durable stamp,
        // that null cutoff filed EVERY later comment under "past", including the question the bot is required
        // to answer, which is how the new-comment handling was defeated by a formatting choice.
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        var postedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture);
        fixture.Store.MarkReviewPosted(run.Id, postedAt);
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "UNPREFIXED-BOT-SUMMARY",
                "revobot",
                IsActive: true,
                PublishedAt: postedAt.AddMinutes(-5),
                ThreadId: "th-old"
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "20",
                "NEW-HUMAN-QUESTION after the stamp",
                "alice",
                IsActive: true,
                PublishedAt: postedAt.AddHours(1),
                ThreadId: "th-human"
            )
        );

        var text = await RenderReviewInputAsync(fixture, run);

        var (past, fresh) = DedupSections(text);
        fresh
            .Should()
            .Contain(
                "NEW-HUMAN-QUESTION",
                "the stamp says the bot last spoke at 10:00, so a comment an hour later is new by definition"
            );
        past.Should().Contain("UNPREFIXED-BOT-SUMMARY", "and everything before the stamp is past");
    }

    [Fact]
    public async Task Reviewed_keeps_a_bot_comment_newer_than_the_stamp_as_the_cutoff()
    {
        // The cutoff is the LATER of the two signals, not "the stamp wins". The agent-inline posting path
        // returns no receipt to the host, so nothing stamps it — the only evidence such a comment ever existed
        // is its body. Take the stamp alone and a human reply that came AFTER an inline comment is misfiled as
        // new-since-last-review, so the bot re-answers a thread it already answered.
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        var stampedAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture);
        fixture.Store.MarkReviewPosted(run.Id, stampedAt);
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "[Revobot] INLINE-FINDING",
                "revobot",
                IsActive: true,
                PublishedAt: stampedAt.AddHours(5),
                ThreadId: "th-bot"
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "20",
                "BETWEEN-THE-TWO",
                "alice",
                IsActive: true,
                PublishedAt: stampedAt.AddHours(2),
                ThreadId: "th-between"
            )
        );

        var text = await RenderReviewInputAsync(fixture, run);

        var (past, fresh) = DedupSections(text);
        past.Should()
            .Contain(
                "BETWEEN-THE-TWO",
                "the bot spoke again at +5h, so a comment at +2h is older than the bot's last word and is past"
            );
        fresh.Should().NotContain("BETWEEN-THE-TWO");
    }

    [Fact]
    public async Task Reviewed_keeps_the_stamp_as_the_cutoff_when_it_is_newer_than_any_bot_comment()
    {
        // The other direction of the same MAX, so neither term can be dropped without a test noticing.
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        var botSpokeAt = DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture);
        fixture.Store.MarkReviewPosted(run.Id, botSpokeAt.AddHours(5));
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "10",
                "[Revobot] OLD-PREFIXED-FINDING",
                "revobot",
                IsActive: true,
                PublishedAt: botSpokeAt,
                ThreadId: "th-bot"
            )
        );
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "20",
                "BETWEEN-THE-TWO",
                "alice",
                IsActive: true,
                PublishedAt: botSpokeAt.AddHours(2),
                ThreadId: "th-between"
            )
        );

        var text = await RenderReviewInputAsync(fixture, run);

        var (past, fresh) = DedupSections(text);
        past.Should()
            .Contain(
                "BETWEEN-THE-TWO",
                "the stamp proves the bot posted at +5h, so a comment at +2h predates the bot's last word"
            );
        fresh.Should().NotContain("BETWEEN-THE-TWO");
    }

    [Fact]
    public async Task Posted_stamps_the_cutoff_only_once_delivery_is_proven()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();
        fixture.Store.GetLastPostedReviewAt(run.RepoId, run.PrId).Should().BeNull("nothing has been posted yet");

        await RunAllStagesAsync(fixture, run);

        fixture.Publisher.PostCount.Should().Be(1, "this fixture authorizes live posting");
        fixture
            .Store.GetLastPostedReviewAt(run.RepoId, run.PrId)
            .Should()
            .NotBeNull("the review reached the PR, so the next review's cutoff must know when");
    }

    [Fact]
    public async Task Posted_does_not_move_the_cutoff_forward_when_a_retry_replays_the_same_post()
    {
        // The Posted stage is ALWAYS retried after a terminal failure (retention, a stale index.lock, a
        // publisher blip), and the retry is resumed by the very fast path this branch documents. On that retry
        // ReviewPoster correctly REPLAYS rather than re-posting — but IsDeliveryProven(ReplayNoOp) is true by
        // design, because the replay proves a comment exists on the PR. What it does NOT prove is that the
        // comment arrived just now. Stamping the retry's clock would drag the cutoff across the whole outage
        // and reclassify every comment left during it as PAST, so the next review never answers them — exactly
        // the failure the v5 migration comment calls strictly worse than the null it replaces.
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);
        var firstStamp = fixture.Store.GetLastPostedReviewAt(run.RepoId, run.PrId);
        firstStamp.Should().NotBeNull("the first pass delivered, so it must have stamped");

        // Long enough that UtcNow is certain to have moved on (the Windows system timer is ~15.6ms), and the
        // assertion below proves it actually did rather than trusting the delay. Without that guard a clock
        // that had not ticked would make this test pass against a stamp rewritten on every pass.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        DateTimeOffset
            .UtcNow.Should()
            .BeAfter(
                firstStamp!.Value,
                "the gap must be observable, or this test cannot tell a frozen cutoff from a rewritten one"
            );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        fixture.Publisher.PostCount.Should().Be(1, "the retry replays the outbox row; it does not post again");
        fixture
            .Store.GetLastPostedReviewAt(run.RepoId, run.PrId)
            .Should()
            .Be(
                firstStamp,
                "the cutoff records when the bot last SPOKE, and the retry did not speak — it found the words "
                    + "already there"
            );
    }

    [Fact]
    public async Task Posted_leaves_no_cutoff_when_posting_is_switched_off_entirely()
    {
        // The posting-disabled path: no post is even attempted, so there is nothing that could be mistaken for
        // delivery. This does NOT prove "an attempt is not evidence" — postOutcome is null here, so the stamp
        // site is never reached. The test that carries that proof is the one below, where a post IS attempted
        // and comes back CollectedOnly; mutation found this one green against a stamp written unconditionally.
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        fixture.Publisher.PostCount.Should().Be(0, "this fixture does not authorize live posting");
        fixture
            .Store.GetLastPostedReviewAt(run.RepoId, run.PrId)
            .Should()
            .BeNull("collecting a review is not delivering it, and only delivery may move the cutoff");
    }

    // ── #225 item 2: a failed listing withdraws posting on a re-review, not on a first review ─────

    [Fact]
    public async Task Reviewed_declines_to_post_when_the_listing_fails_on_a_pr_that_already_carries_a_review()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();
        var seededCutoff = DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture);
        fixture.Store.MarkReviewPosted(run.Id, seededCutoff);
        fixture.Publisher.ListFailure = new HttpRequestException("the provider is having a moment");

        await RunAllStagesAsync(fixture, run);

        fixture.Publisher.ListCallCount.Should().BeGreaterThan(0, "the listing was attempted and it threw");
        fixture
            .Publisher.PostCount.Should()
            .Be(
                0,
                "the review was written without knowing what is already on the PR, and this PR already carries a "
                    + "review — posting it blind is how the duplicate-review spam comes back"
            );
        fixture.Store.WasDedupContextLost(run.Id).Should().BeTrue();

        // AN ATTEMPT IS NOT EVIDENCE, and this is the only place that proves it: posting is authorized by
        // configuration here, so a post IS attempted and comes back CollectedOnly. A stamp written on the
        // outcome rather than on proven delivery would drag the cutoff to now — burying every comment made
        // since the real one, which is strictly worse than the null the stamp replaced.
        fixture
            .Store.GetLastPostedReviewAt(run.RepoId, run.PrId)
            .Should()
            .Be(
                seededCutoff,
                "nothing was delivered this round, so the cutoff is still where the last delivery left it"
            );
    }

    [Fact]
    public async Task Reviewed_still_posts_when_the_listing_fails_on_a_pr_that_has_never_been_reviewed()
    {
        // The non-vacuity half. A suppression that fires on EVERY fetch failure would trade a real first review
        // for a hypothetical duplicate — there is nothing on this PR to duplicate.
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();
        fixture.Publisher.ListFailure = new HttpRequestException("the provider is having a moment");

        await RunAllStagesAsync(fixture, run);

        fixture.Publisher.ListCallCount.Should().BeGreaterThan(0, "the listing was attempted and it threw");
        fixture.Publisher.PostCount.Should().Be(1, "a first review has nothing to duplicate, so it still ships");
        fixture.Store.WasDedupContextLost(run.Id).Should().BeFalse();
    }

    [Fact]
    public async Task A_post_mode_run_that_lost_its_dedup_context_completes_instead_of_holding_the_stage_open()
    {
        // The trap this fix sets for itself. The delivery-truthfulness gate holds the Posted stage open when a
        // post-mode run produced no delivery evidence — and a suppressed run never can. Posted is not a governed
        // stage, so without teaching the gate about this suppression the run would throw on every attempt and
        // spin forever over a decision it cannot influence, which is the same hot-loop the EnableCommentPosting
        // escape hatch already exists to prevent.
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun(mode: "post");
        fixture.Store.MarkReviewPosted(
            run.Id,
            DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture)
        );
        fixture.Publisher.ListFailure = new HttpRequestException("the provider is having a moment");

        var act = async () => await RunAllStagesAsync(fixture, run);

        await act.Should()
            .NotThrowAsync("collecting IS the truthful outcome for a run that was never authorized to post this round");
        fixture.Publisher.PostCount.Should().Be(0);
    }

    /// <summary>Drives the two stages that build the review prompt and returns the text the agent received.</summary>
    private static async Task<string> RenderReviewInputAsync(Fixture fixture, ReviewRun run)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        return fixture
            .Factory.CreatedAgents.Should()
            .ContainSingle()
            .Subject.ReceivedInputs[0]
            .Messages.OfType<TextMessage>()
            .Single()
            .Text;
    }

    /// <summary>Seeds the bot's own last finding, which is the cutoff that splits "past" from "new".</summary>
    private static void SeedBotCutoff(Fixture fixture) =>
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Cutoff.cs",
                "1",
                "[Revobot] LAST-REVIEW-CUTOFF",
                "revobot",
                IsActive: true,
                PublishedAt: DateTimeOffset.Parse("2026-07-20T10:00:00Z", CultureInfo.InvariantCulture),
                ThreadId: "th-cutoff"
            )
        );

    [Fact]
    public async Task Reviewed_bounds_the_past_section_to_what_is_not_reserved_for_the_new_one()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // A PR with a long history: 60 past comments of ~900 chars is ~54,000 characters, well past the whole
        // block's allowance and more than twice what the past section may spend on its own.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 60, bodyChars: 900, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 1, bodyChars: 100, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, _) = DedupSections(text);

        // The slack covers the single comment that was mid-render when the allowance ran out plus the section
        // heading; what is being pinned is the ORDER of magnitude — the past section spends the unreserved half,
        // not the whole block. Hand it the full budget and this doubles.
        past.Length.Should()
            .BeLessThan(
                BlockCharBudget - NewSectionReservation + 3_000,
                "the past section may only spend what is not reserved for the new-comments section"
            );

        // Non-vacuity: an empty section satisfies the bound on its own, and an empty section is what a block that
        // failed to render at all would produce.
        past.Should().Contain("PAST-000", "the past section really did render");
        text.Should()
            .Contain(
                "more comment(s) not shown",
                "what did not fit is reported as a count rather than dropped silently"
            );
    }

    [Fact]
    public async Task Reviewed_keeps_the_new_comments_section_even_when_the_past_history_is_enormous()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The failure this guards: a PR with hundreds of historical comments spends the whole block on history and
        // pushes out the question addressed to the bot — which the guidance tells it, in bold terms, to answer.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 60, bodyChars: 900, isNew: false);
        fixture.Publisher.ExistingComments.Add(
            new ExistingReviewComment(
                "src/Foo.cs",
                "20",
                "Alice asks: NEW-QUESTION-FOR-THE-BOT?",
                "alice",
                IsActive: true,
                PublishedAt: DateTimeOffset.Parse("2026-07-21T09:00:00Z", CultureInfo.InvariantCulture),
                ThreadId: "th-human"
            )
        );

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, fresh) = DedupSections(text);

        fresh
            .Should()
            .Contain(
                "NEW-QUESTION-FOR-THE-BOT",
                "the new-comments section holds a reserved floor of the block that history cannot spend"
            );

        // The distinguishing half: the history really was large enough to have swallowed the block. Without this
        // the assertion above passes on a PR whose history happened to fit, proving nothing about the reservation.
        past.Should().Contain("more comment(s) not shown", "the past history genuinely overran its allowance");
    }

    [Fact]
    public async Task Reviewed_lets_the_new_comments_section_spend_the_whole_block_when_it_needs_it()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The new section claims the block first, so the reservation is never its ceiling. On the common PR —
        // short history, busy recent discussion — it should use the whole block rather than be held to half.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 1, bodyChars: 100, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 40, bodyChars: 900, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (_, fresh) = DedupSections(text);

        fresh
            .Length.Should()
            .BeGreaterThan(
                NewSectionReservation + 5_000,
                "the new section draws on the whole block, not on a reserved share of it — held to that share it "
                    + "would stop around 20,000 characters"
            );
        fresh
            .Length.Should()
            .BeLessThan(
                BlockCharBudget + 3_000,
                "…but never more than the whole block, which is the point of one budget"
            );
    }

    [Fact]
    public async Task Reviewed_counts_rendered_comments_across_both_sections_against_one_cap()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Small bodies, so the character budget is nowhere near binding and the COMMENT cap is what is measured.
        // 500 seeded across the two sections: applied per-section the block renders 500, applied once it renders
        // the documented 400.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 250, bodyChars: 5, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 250, bodyChars: 5, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, fresh) = DedupSections(text);

        RenderedCommentCount(past + fresh)
            .Should()
            .Be(BlockCommentBudget, "the comment cap bounds the whole block, not each section separately");

        // Both sections are represented, so the 400 is not one section rendering the entire cap.
        RenderedCommentCount(past).Should().BeGreaterThan(0);
        RenderedCommentCount(fresh).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Reviewed_renders_the_whole_conversation_of_the_busiest_observed_pr()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // 201 comments is the measured ceiling across a 300-PR survey — the busiest real conversation this daemon
        // has been pointed at. The old cap of 120 cut it, and cut 5 of the 9 busiest PRs with it, which is the
        // whole reason for the raise. Bodies are tiny so the CHARACTER budget cannot be what lets this through:
        // if the comment cap were still 120 this renders 120 no matter how small the bodies are.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 149, bodyChars: 5, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 51, bodyChars: 5, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, fresh) = DedupSections(text);

        RenderedCommentCount(past + fresh)
            .Should()
            .Be(
                201,
                "the busiest PR the daemon has measured must fit inside the block, not be truncated by it (149 past "
                    + "+ the bot's own cutoff comment + 51 new)"
            );

        // Non-vacuity: the last comment of each section is present, so the count is not 201 lines of omission
        // notes, and the past section — the one that renders against what the new section left — is complete too.
        past.Should().Contain("PAST-148");
        fresh.Should().Contain("NEW-050");
        (past + fresh)
            .Should()
            .NotContain("more comment(s)", "nothing was cut, so neither section should have written an omission note");
    }

    [Fact]
    public async Task Reviewed_lets_the_new_section_claim_the_block_before_the_past_history_does()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // The case where claim order is the ONLY thing that decides the outcome. Both sections want more than
        // half the block: history wants ~38,000 chars and the new discussion wants ~28,500 of a 40,000 block.
        //
        //   past-first: history spends the unreserved 20,000, leaves nothing, and the new section is held to its
        //               20,000 reservation — so it stops around comment 21 and NEW-029 is never rendered.
        //   NEW-first:  the new discussion takes the ~28,500 it needs from the whole block and renders complete;
        //               history takes the ~11,500 remainder and is the section that gets cut.
        //
        // Both orders spend one shared budget and both announce what they dropped, so only the identity of the
        // truncated section separates them.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 40, bodyChars: 900, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 30, bodyChars: 900, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, fresh) = DedupSections(text);

        fresh
            .Should()
            .Contain(
                "NEW-029",
                "the new section claims the block first, so it renders its whole conversation even though the "
                    + "history that precedes it wanted more than the block had left"
            );

        // The distinguishing half: the block really was oversubscribed, so "NEW-029 rendered" is a statement
        // about claim order rather than about a PR that happened to fit.
        past.Should()
            .Contain("more comment(s)", "the history is the section that absorbs the truncation under this order");
        past.Should().Contain("PAST-000", "non-vacuity: the past section did render up to its cut");
    }

    [Fact]
    public async Task Reviewed_states_the_listing_caps_in_the_rendered_block()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // A listing the model reads as exhaustive is one it reasons from as exhaustive. The block must say what
        // its ceiling is, in the numbers actually enforced, so a section that stops just under the bound is not
        // read as a PR that had nothing more to say.
        SeedBotCutoff(fixture);
        SeedBulkComments(fixture, "PAST", count: 2, bodyChars: 20, isNew: false);
        SeedBulkComments(fixture, "NEW", count: 2, bodyChars: 20, isNew: true);

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;

        text.Should()
            .Contain("at most 400 comment(s)", "the comment ceiling the renderer enforces is the one the block states");
        text.Should()
            .Contain(
                "40,000 characters",
                "…and so is the character ceiling, which is the one that usually binds first"
            );
        text.Should()
            .Contain(
                "claims that allowance first",
                "the model is told WHICH section the allowance is spent on first, so it can read a short past "
                    + "section as a budget decision rather than as a quiet PR"
            );
        text.Should()
            .Contain(
                "INCOMPLETE",
                "and it is told what an omission note means for its own conclusions — the reason for saying any of "
                    + "this"
            );
    }

    [Fact]
    public async Task Reviewed_cuts_a_thread_that_does_not_fit_and_says_how_much_is_missing()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // One enormous conversation on a single thread. Rendering a started thread WHOLE — the old behaviour — is
        // how a single pathological thread overran a bound the caller believed it had set. No bot cutoff is seeded,
        // so nothing is "new" and the whole conversation lands in the past section.
        SeedBulkComments(fixture, "PAST", count: 40, bodyChars: 900, isNew: false, threadId: "th-one");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var reviewAgent = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject;
        var text = reviewAgent.ReceivedInputs[0].Messages.OfType<TextMessage>().Single().Text;
        var (past, _) = DedupSections(text);

        past.Should()
            .Contain(
                "more comment(s) in this thread not shown",
                "a thread is cut where the budget runs out, and the cut is named so the reviewer knows it is not "
                    + "reading the whole conversation"
            );
        past.Length.Should()
            .BeLessThan(
                BlockCharBudget - NewSectionReservation + 3_000,
                "…which is what stops one thread from overrunning the section's allowance"
            );
        past.Should().Contain("PAST-000", "non-vacuity: the thread did render up to the cut");
    }

    [Fact]
    public async Task Reviewed_escalates_to_the_bigger_model_then_diff_only_when_the_context_window_overflows()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // EVERY tool-assisted attempt is rejected with the context-window 400 the daemon saw live (the PR diff +
        // fanned-out sub-agent results overflow the window). So the escalation ladder runs to the end: (1) base
        // model tool-assisted → overflow; (2) escalate to the bigger-window model tool-assisted → overflow;
        // (3) diff-only on the bigger model → succeeds, yielding a leaner review instead of producing nothing.
        fixture.Factory.ThrowWhenToolAssisted = new HttpRequestException(
            "HTTP request failed with status BadRequest (Bad Request). Response body: "
                + "{\"error\":{\"message\":\"Your input exceeds the context window of this model.\"}}"
        );
        fixture.Factory.DefaultText = "## Review (diff-only)\nMust: null check missing in Foo.cs:10.";

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Three attempts: base tool-assisted, escalation-model tool-assisted, escalation-model diff-only.
        fixture.Factory.ToolContexts.Should().HaveCount(3);
        fixture.Factory.ToolContexts[0].Should().NotBeNull();
        fixture.Factory.ToolContexts[1].Should().NotBeNull();
        fixture.Factory.ToolContexts[2].Should().BeNull();

        // Attempts 2 & 3 escalate to the bigger-window model (OverflowEscalationModelId default gpt-5.6-terra).
        fixture.Factory.ModelIds[0].Should().Be(run.ModelId);
        fixture.Factory.ModelIds[1].Should().Be("gpt-5.6-terra");
        fixture.Factory.ModelIds[2].Should().Be("gpt-5.6-terra");

        // Each attempt runs on a DISTINCT thread so it starts a clean conversation rather than reloading the
        // overflowing history that just blew the window.
        fixture.Factory.ThreadIds.Distinct().Should().HaveCount(3);

        // The review artifact holds the diff-only review — the run produced content, not a silent nothing.
        var artifact = fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind)
            .Subject;
        artifact.Payload.Should().Contain("diff-only");
    }

    [Fact]
    public async Task Reviewed_escalation_to_the_bigger_model_succeeds_tool_assisted_without_dropping_to_diff_only()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun() with { ModelId = "gpt-5.6-luna" };

        // ONLY the base model overflows; the bigger-window escalation model succeeds tool-assisted — so the
        // review stays grounded (keeps its sub-agents) instead of degrading to diff-only.
        fixture.Factory.ThrowWhenToolAssisted = new HttpRequestException(
            "HTTP request failed with status BadRequest (Bad Request). Response body: "
                + "{\"error\":{\"message\":\"Your input exceeds the context window of this model.\"}}"
        );
        fixture.Factory.ThrowOnlyForModel = "gpt-5.6-luna";
        fixture.Factory.DefaultText = "## Review (grounded on the bigger model)\nShould: rename X.";

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Two attempts, both tool-assisted: base (overflow) then escalation model (succeeds). No diff-only fallback.
        fixture.Factory.ToolContexts.Should().HaveCount(2);
        fixture.Factory.ToolContexts[0].Should().NotBeNull();
        fixture.Factory.ToolContexts[1].Should().NotBeNull();
        fixture.Factory.ModelIds[0].Should().Be("gpt-5.6-luna");
        fixture.Factory.ModelIds[1].Should().Be("gpt-5.6-terra");

        var artifact = fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind)
            .Subject;
        artifact.Payload.Should().Contain("grounded on the bigger model");
    }

    /// <summary>
    /// The lifecycle checkpoint is what <c>DaemonCorpusReader</c> attributes an eval candidate to, so the
    /// model it names has to be one the transport was actually told to run. Against a factory that FORWARDS
    /// the per-call id, the escalation target is that model — the whole point of #510 — and the checkpoint
    /// must move with the ladder rather than keep naming the model the run was seeded with.
    /// </summary>
    [Fact]
    public async Task Reviewed_checkpoints_the_escalation_model_when_the_loop_factory_forwards_it()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun() with { ModelId = "gpt-5.6-luna" };

        // A transport that can select per call: the id handed to Create is the id that runs.
        fixture.Factory.HonoursRequestedModelId = true;
        fixture.Factory.ThrowWhenToolAssisted = new HttpRequestException(
            "HTTP request failed with status BadRequest (Bad Request). Response body: "
                + "{\"error\":{\"message\":\"Your input exceeds the context window of this model.\"}}"
        );
        fixture.Factory.ThrowOnlyForModel = "gpt-5.6-luna";

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Non-vacuity: the ladder really did escalate, so the two ids on offer genuinely differ here.
        fixture.Factory.ModelIds.Should().Equal("gpt-5.6-luna", "gpt-5.6-terra");
        LatestCheckpointModelId(fixture, run).Should().Be("gpt-5.6-terra");
    }

    /// <summary>
    /// And against the only factory production has, which does NOT forward it, the checkpoint keeps
    /// <c>review_run.model_id</c>. The escalation target there is a request that never left the process —
    /// S2S provision carries no model field — so recording it would credit the candidate to a model the
    /// review host was never asked for, silently, a wrong id being indistinguishable from a right one at
    /// the corpus layer.
    /// </summary>
    [Fact]
    public async Task Reviewed_keeps_the_seeded_model_on_the_checkpoint_when_the_loop_factory_discards_the_override()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun() with { ModelId = "gpt-5.6-luna" };

        // The production shape: Create ignores its modelId argument entirely.
        fixture.Factory.HonoursRequestedModelId = false;
        fixture.Factory.ThrowWhenToolAssisted = new HttpRequestException(
            "HTTP request failed with status BadRequest (Bad Request). Response body: "
                + "{\"error\":{\"message\":\"Your input exceeds the context window of this model.\"}}"
        );
        fixture.Factory.ThrowOnlyForModel = "gpt-5.6-luna";

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Non-vacuity: the executor still ASKS for the bigger model — the ladder is unchanged, and this run
        // is the escalated case. What the gate governs is only what gets written down.
        fixture.Factory.ModelIds.Should().Equal("gpt-5.6-luna", "gpt-5.6-terra");
        LatestCheckpointModelId(fixture, run).Should().Be("gpt-5.6-luna");
    }

    /// <summary>
    /// The model on the LAST <c>review-provisional</c> checkpoint — the rung whose output was kept, which is
    /// the row <c>DaemonCorpusReader.EffectiveGeneratorModelId</c> reads.
    /// </summary>
    private static string? LatestCheckpointModelId(Fixture fixture, ReviewRun run)
    {
        var checkpoints = fixture
            .Store.GetArtifacts(run.Id)
            .Where(a => a.ArtifactKind == DaemonReviewStageExecutor.ProvisionalReviewArtifactKind)
            .ToList();
        checkpoints.Should().NotBeEmpty("the attempt must have checkpointed its lifecycle at all");
        var payload = JsonSerializer.Deserialize<ReviewArtifactPayload>(checkpoints[^1].Payload);
        payload.Should().NotBeNull();
        payload!.Lifecycle.Should().NotBeNull("the model claim lives on the lifecycle identity");
        return payload.Lifecycle!.ModelId;
    }

    [Fact]
    public async Task Reviewed_templates_the_notes_dir_into_the_review_system_prompt()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The reviewer is TOLD exactly where it may read/write for THIS run — not left to guess — via the
        // templated "Workspace layout" section of the review system prompt. The notes dir must be the
        // identical value the tool context scoped Write/Edit/Bash to (asserted above).
        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().Contain("/workspace/store/repos/LmDotnetTools");
        profile.SystemPrompt.Should().Contain("cross-repo store at /workspace/store");
        profile.SystemPrompt.Should().Contain("/workspace/store/PRs/lmdotnettools-118");
        profile.SystemPrompt.Should().MatchRegex("(?i)only writable location");
    }

    [Fact]
    public async Task Reviewed_templates_rereview_context_and_prior_notes_files_into_the_system_prompt()
    {
        using var fixture = Fixture.Create();

        // A prior round already completed for this PR at an older head — the current run is round 2.
        var repoId = fixture.Store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        _ = fixture.Store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "sha-old",
                BaseSha = "base-sha",
                TriggerWatermark = "wm-0",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                Stage = ReviewStage.Posted,
                WorkflowStatus = WorkflowStatus.Completed,
                PrLifecycleState = PrLifecycleState.Open,
            }
        );
        var run = fixture.SeedRun(); // head-sha "head-sha" — this round's head

        // The prior round's own notes live on the LEASED SLOT's host store checkout (where
        // CommitPooledNotesAsync wrote them) and MUST be listed HOST-side via _slotWorkspace.HostFileSystem +
        // lease.Prepared.NotesDir — NOT the boot-lifetime sandbox session (fixture.BootFileSystem), which the
        // gateway never registers for a pooled run (so it 404s) and whose first use would bind a boot gateway
        // session that collides with the per-run review MCP session, failing the whole review. Seeding host-side
        // (not boot) is the regression guard: reading prior notes through the boot fs would find nothing here.
        fixture.HostFileSystem.Seed("/pool/slot-0/store/PRs/lmdotnettools-118/PR_Findings_01.md", "prior findings");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().MatchRegex("(?i)RE-REVIEW");
        profile.SystemPrompt.Should().Contain("round 02");
        profile.SystemPrompt.Should().Contain("sha-old"); // the previously-reviewed commit
        profile.SystemPrompt.Should().Contain("head-sha"); // the current head
        profile.SystemPrompt.Should().Contain("PR_Findings_01.md"); // prior notes file, read-first
    }

    [Fact]
    public async Task Reviewed_mounts_the_agent_session_over_the_leased_slot()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // A pooled run provisions the review agent's session by mounting it OVER the slot leased in
        // ContextReady (GetOrCreateForSlotAsync) — never the per-run mount — and the provisioner saw the
        // very slot that was leased (index 0).
        fixture.Provisioner.GetOrCreateForSlotCalls.Should().Be(1);
        fixture.Provisioner.LastSlot.Should().NotBeNull();
        fixture.Provisioner.LastSlot!.Index.Should().Be(0);
    }

    [Fact]
    public async Task S2S_review_releases_before_preparing_the_workspace_after_a_restart()
    {
        using var fixture = Fixture.CreateS2S(slots: 2);
        var run = fixture.SeedRun();

        // Process A persists ContextReady with slot 0, then disappears with all process-local lease/workspace caches.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(2);
        fixture
            .Factory.WorkspaceIds.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "ws-review-slot-1",
                "the hosted workspace must be prepared from the newly leased slot, not a cached bare PR clone"
            );
        fixture.S2SGit.Commands.Should().BeEmpty("slot adoption must not run the fallback clone preparer");
    }

    /// <summary>
    /// The judge is the LAST stage that prepares an S2S workspace, and it was the only post-<c>ContextReady</c>
    /// stage without the resume-safety re-lease that <c>ReviewAsync</c> and <c>PostAsync</c> carry. A restart
    /// between <c>Reviewed</c> and <c>Judged</c> therefore reached <c>EnsurePreparedAsync</c> with no lease, and
    /// the lease-less branch host-cloned a bare per-PR checkout plus a PERMANENT LmStreaming workspace record —
    /// the very leak <c>FetchContextAsync</c> fails closed to prevent, reached two stages later, on a review
    /// that was proceeding perfectly normally.
    /// </summary>
    [Fact]
    public async Task S2S_judge_after_a_restart_re_leases_the_slot_rather_than_minting_an_unmanaged_workspace()
    {
        using var fixture = Fixture.CreateS2S(slots: 2, judge: true);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.JudgeProfileId] =
            "{\"score\": 8, \"rationale\": \"Solid.\"}";
        var run = fixture.SeedRun();

        // Process A reviews on slot 0, then disappears with all process-local lease/workspace caches.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        fixture
            .Pool.LeaseCount.Should()
            .Be(2, "the resumed judge re-leases a slot because the prior lease died with the process");
        fixture
            .Factory.WorkspaceIds.Should()
            .Equal(
                ["ws-review-slot-0", "ws-review-slot-1"],
                "the judge's hosted conversation is mounted over the slot it holds NOW, not a bare per-PR clone"
            );
        fixture.S2SGit.Commands.Should().BeEmpty("no unmanaged per-PR host clone may be created for a resumed judge");
        fixture
            .S2SHandler.Requests.Should()
            .NotContain(
                r => r.Body != null && r.Body.Contains("pr-118", StringComparison.Ordinal),
                "no permanent per-PR LmStreaming workspace may be minted for a resumed judge"
            );

        // Non-vacuity: the judge really ran over that workspace, so the assertions above are about a stage that
        // did its work — not about one that returned early and prepared nothing.
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind, "the resumed judge still graded the review");
    }

    /// <summary>
    /// The control for the case above, and the reason its lease count means what it claims: with the judge OFF
    /// (production's default) the same restart must lease nothing and prepare nothing, because the stage
    /// returns before it asks for a workspace. A re-lease that fired here would be pure cost — a slot
    /// preparation for a stage that does no work.
    /// </summary>
    [Fact]
    public async Task S2S_judge_disabled_after_a_restart_leases_nothing_and_prepares_no_workspace()
    {
        using var fixture = Fixture.CreateS2S(slots: 2);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1, "the disabled judge returns before it needs a slot");
        fixture
            .Factory.WorkspaceIds.Should()
            .ContainSingle()
            .Which.Should()
            .Be("ws-review-slot-0", "only the review stage prepared a workspace");
        fixture.S2SGit.Commands.Should().BeEmpty("nothing was prepared at all, so no clone either");
        fixture.Store.GetArtifacts(run.Id).Should().NotContain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind);
    }

    /// <summary>
    /// The other half of the fix, and the half that survives a failed re-lease. Restoring the lease is what the
    /// stages now try; this is what happens when that try DECLINES — here because the store the resumed run
    /// leases no longer carries the reviewed repo. <c>EnsurePreparedAsync</c> is the single place a preparation
    /// is made, so refusing there is what stops any caller — this judge, a future stage, the re-lease that
    /// silently returned false — from answering a lost lease with an unmanaged clone nothing ever reclaims.
    /// </summary>
    [Fact]
    public async Task S2S_fails_closed_when_a_resumed_stage_cannot_re_lease_a_slot()
    {
        using var fixture = Fixture.CreateS2S(slots: 2, judge: true);
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The slot the resumed run will lease next declares a DIFFERENT submodule, so the pooled re-lease
        // declines and the judge arrives at EnsurePreparedAsync still holding nothing.
        fixture.HostFileSystem.Seed(
            "/pool/review-slot-1/store/.gitmodules",
            "[submodule \"other\"]\n\tpath = repos/other\n\turl = https://github.com/achieveai/other.git\n"
        );
        var resumed = fixture.BuildExecutor();

        var act = () => resumed.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().MatchRegex("(?i)no pooled review slot is leased");
        fixture
            .S2SGit.Commands.Should()
            .BeEmpty("the refusal comes BEFORE any host git — no unmanaged per-PR clone is created");
        fixture
            .S2SHandler.Requests.Should()
            .NotContain(
                r => r.Body != null && r.Body.Contains("pr-118", StringComparison.Ordinal),
                "the refusal comes BEFORE any workspace REST call — no permanent per-PR workspace is minted"
            );
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .NotContain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind, "the stage failed, so it graded nothing");
    }

    [Fact]
    public async Task S2S_retry_in_the_same_process_binds_the_slot_it_holds_now_not_the_one_it_prepared_from()
    {
        using var fixture = Fixture.CreateS2S(slots: 2);
        var run = fixture.SeedRun();

        // Attempt 1 leases slot 0 and prepares that slot as the workspace. The orchestrator then releases the
        // lease in its terminal finally, which it runs on EVERY outcome — including the failure→RetryPending
        // rethrow this models.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);

        // Attempt 2 re-enters with the SAME run id on the SAME executor — which is registered as a singleton, so
        // unlike the restart case above, its per-run caches are all still there. This is the shape of both a
        // retry and a StrandedRunReconciler resume.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        fixture
            .Pool.Leased.Select(s => s.Index)
            .Should()
            .Equal([0, 1], "the retry gets whatever slot is free, which is not the one it returned");

        // The workspace is what the hosted agent's /workspace/store/... paths resolve through, and slot 0 is by
        // now some other PR's checkout. Reusing the first attempt's workspace would have the review read one
        // PR while reporting on another — a wrong review, not a failed one.
        fixture
            .Factory.WorkspaceIds.Should()
            .Equal(
                ["ws-review-slot-0", "ws-review-slot-1"],
                "each attempt binds the workspace of the slot it actually holds"
            );
    }

    [Fact]
    public async Task Reviewed_re_leases_a_slot_when_resuming_after_a_restart_dropped_the_in_memory_lease()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Process A: ContextReady leases slot 0 and persists the context artifact to the shared store.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.LeaseCount.Should().Be(1);

        // A restart drops the in-memory _leasedReviews (the persisted context + Stage survive in the store),
        // so the NEXT process resumes straight into Reviewed with NO recorded lease. Seed the gitmodules for
        // the slot the resumed run will lease next (slot-1), mirroring slot-0.
        fixture.HostFileSystem.Seed(
            "/pool/slot-1/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The resumed review must RE-LEASE a slot and mount the agent session OVER it — never the per-run
        // review-run-{id} mount, which does not exist under the gateway's read-only workspace base and 400s
        // (the silent degrade-to-diff-only these resumed runs were stuck in).
        fixture
            .Pool.LeaseCount.Should()
            .Be(2, "the resumed review re-leases a slot because the prior lease was lost on restart");
        fixture
            .Provisioner.GetOrCreateForSlotCalls.Should()
            .Be(2, "the original context and resumed context each mount their own leased slot once");
        fixture
            .Provisioner.GetOrCreateCalls.Should()
            .Be(0, "the resumed review must never fall back to the broken per-run mount");
    }

    /// <summary>
    /// The in-process counterpart of the S2S judge restart: the re-lease is not conditioned on the S2S path, so
    /// it fires here too. That uniformity is deliberate and it is not a cost — the terminal <c>Posted</c> stage
    /// re-leases for its own reasons (it commits the notes into the pooled store), so a judge that restores the
    /// lease first hands Posted the slot it would otherwise have leased itself. The pool sees the same two
    /// leases either way; what changes is that no stage in between is left holding nothing.
    /// </summary>
    [Fact]
    public async Task Judged_re_leases_a_slot_after_a_restart_and_the_post_stage_reuses_it()
    {
        using var fixture = Fixture.Create(slots: 2, judge: true);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.JudgeProfileId] =
            "{\"score\": 8, \"rationale\": \"Solid.\"}";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        fixture
            .Pool.LeaseCount.Should()
            .Be(2, "the resumed judge re-leases a slot because the prior lease died with the process");
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind, "the resumed judge still graded the review");

        await resumed.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        fixture
            .Pool.LeaseCount.Should()
            .Be(2, "Posted reuses the lease the judge restored instead of leasing a third slot");
        fixture
            .HostRunner.Commands.Select(Describe)
            .Should()
            .Contain(
                a => a.Contains($"add -- {NotesRelPath}") && a.Contains("/pool/slot-1/store"),
                "the notes are retained into the store of the slot the resumed run actually holds"
            );
        fixture.Pool.ReturnCount.Should().Be(1, "the re-leased slot is returned once, on the terminal stage");
    }

    /// <summary>
    /// The other half of the judge's re-lease condition. Restoring a LOST lease is resume-safety; taking a
    /// second one while the first is still held is a slow leak of the pool's own concurrency — every ordinary
    /// review would consume two slots for one PR, and the notes would then be committed into a store the
    /// review never ran in.
    /// </summary>
    [Fact]
    public async Task Judged_in_the_same_process_reuses_the_lease_it_already_holds()
    {
        using var fixture = Fixture.Create(slots: 2, judge: true);
        fixture.Factory.TextByProfileId[DaemonAgentFactory.JudgeProfileId] =
            "{\"score\": 8, \"rationale\": \"Solid.\"}";
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        fixture.Pool.LeaseCount.Should().Be(1, "the judge already holds the context stage's lease");
        fixture
            .Store.GetArtifacts(run.Id)
            .Should()
            .Contain(a => a.ArtifactKind == JudgeAgent.JudgeArtifactKind, "the judge ran — it just needed no new slot");
    }

    [Fact]
    public async Task Posted_commits_only_the_pr_notes_dir_onto_the_notes_branch_and_never_merges()
    {
        using var fixture = Fixture.Create();
        // First review of the PR: the notes branch does not exist yet, so it is cut from the default branch.
        fixture.HostRunner.OnArgvContains(
            $"rev-parse --verify {Branch}",
            new SandboxCommandResult(1, string.Empty, "unknown revision")
        );
        fixture.HostRunner.OnArgvContains(
            $"rev-parse {Branch}",
            new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty)
        );
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands.Should().Contain(a => a.Contains($"checkout -B {Branch} main"));
        // The commit gate stages ONLY the PR notes dir — never `add -A` (which would stage the moved
        // code-submodule pointer), never a merge, never a branch delete, never a default-branch push.
        commands.Should().Contain(a => a.Contains($"add -- {NotesRelPath}"));
        commands.Should().NotContain(a => a.Contains("add -A"));
        commands.Should().Contain(a => a.Contains("commit -m"));
        commands.Should().Contain(a => a.Contains($"push origin {Branch}"));
        commands.Should().NotContain(a => a.Contains("merge"));
        commands.Should().NotContain(a => a.Contains($"branch -D {Branch}"));
        commands.Should().NotContain(a => a.Contains("push origin main"));

        // The review.md landed inside the per-PR notes dir on the slot's store checkout.
        fixture
            .HostFileSystem.Writes.Should()
            .Contain(p => p.Contains($"/{NotesRelPath}/") && p.EndsWith("review.md"));

        // The retention push outcome is persisted (terminal Posted, carrying the pushed SHA).
        var push = fixture
            .Store.GetOutboxForRun(run.Id)
            .Should()
            .ContainSingle(o => o.Operation == DaemonReviewStageExecutor.PushReviewBotOperation)
            .Subject;
        push.Status.Should().Be(OutboxStatus.Posted);
        push.ProviderResponseId.Should().Be("f00dcafef00dcafe");
    }

    [Fact]
    public async Task Posted_commits_the_finding_disposition_reconciliation_against_the_review_that_shipped()
    {
        // The reconciler is only worth having if it runs on the path that actually commits. Two separable
        // wirings have to hold, and each has its own assertion below, because a mutation to either one is
        // silent: the builder must EMIT the artifact into the committed notes dir, and the commit gate must
        // hand it the review body that shipped. Passing null there still produces a file — one that says the
        // comparison never ran — which is exactly the shape a broken wiring would leave behind.
        using var fixture = Fixture.Create();
        fixture.HostRunner.OnArgvContains(
            $"rev-parse --verify {Branch}",
            new SandboxCommandResult(1, string.Empty, "unknown revision")
        );
        fixture.HostRunner.OnArgvContains(
            $"rev-parse {Branch}",
            new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty)
        );
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        var reconciliation = fixture
            .HostFileSystem.Writes.Should()
            .ContainSingle(
                p =>
                    p.Contains($"/{NotesRelPath}/", StringComparison.Ordinal)
                    && p.EndsWith("PR_Reconciliation_01.md", StringComparison.Ordinal),
                "the disposition record is committed beside review.md, not left in the daemon's log"
            )
            .Subject;

        fixture
            .HostFileSystem.Files[reconciliation]
            .Should()
            .NotContain(
                "## Not compared",
                "the commit gate hands the builder the review body it is committing, so the comparison ran"
            );
    }

    [Fact]
    public async Task Posted_returns_the_leased_slot_on_the_terminal_stage()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        fixture.Pool.ReturnCount.Should().Be(1);
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task Posted_destroys_the_session_before_returning_the_slot()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // The sandbox session is mounted OVER the slot, so it must be torn down BEFORE the slot is returned to
        // the pool — otherwise a lingering sub-agent git op could race the next lease's clean-on-entry on the
        // same store (the concurrency window flagged in review #180).
        fixture.CleanupOrder.Should().ContainInOrder("destroy", "return");
    }

    [Fact]
    public async Task S2S_returns_the_slot_without_destroying_a_session_the_daemon_does_not_own()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // The inverse of the two cases above, and the reason both teardown sites are guarded on S2S. There,
        // BuildToolContextAsync returns BEFORE provisioning, so the daemon owns no session to destroy — while
        // the container that does exist belongs to the review host and must OUTLIVE the run: the posted
        // comment's ?threadId= deep-link is the entire reason this path exists, and tearing the conversation
        // down at teardown would 404 that link the moment the review finished.
        fixture.CleanupOrder.Should().NotContain("destroy");
        fixture
            .CleanupOrder.Should()
            .ContainSingle()
            .Which.Should()
            .Be("return", "the slot still goes back to the pool — only the session teardown is skipped");
    }

    [Fact]
    public async Task ReleaseReviewLease_destroys_the_session_before_returning_the_slot()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // Lease a slot (ContextReady) and provision the session (Reviewed), then simulate a cancel/fail before
        // Posted: the orchestrator's terminal ReleaseReviewLeaseAsync must tear the session down before returning
        // the slot, so no session-side work races the next lease's clean-on-entry (review #180).
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);

        fixture.CleanupOrder.Should().ContainInOrder("destroy", "return");
    }

    /// <summary>
    /// Issue #218 item 11 — the lease record was removed from <c>_leasedReviews</c> BEFORE the teardown and
    /// the pool return ran. That removal is what makes a second release a no-op, so once it has happened
    /// nothing else can ever give the slot back: a throw between it and <c>ReturnAsync</c> leaks a pool slot
    /// permanently, and the daemon loses that capacity for the rest of the process's life.
    /// <para>
    /// Production's provisioner swallows its own failures today, and the code that does carries a comment
    /// calling that swallow load-bearing. Depending on it means the leak is one refactor away in a DIFFERENT
    /// class; the return must hold on its own.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ReleaseReviewLease_returns_the_slot_even_when_the_session_teardown_throws()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        fixture.Provisioner.DestroyFailure = new InvalidOperationException("gateway refused the unlink");

        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);

        fixture
            .Pool.ReturnCount.Should()
            .Be(1, "a slot that is never returned is pool capacity the daemon loses permanently");
        fixture
            .CleanupOrder.Should()
            .ContainInOrder(
                ["destroy", "return"],
                "the teardown is still attempted first; only its failure is contained"
            );
    }

    /// <summary>
    /// Issue #472 item 4. The containment above excluded <see cref="OperationCanceledException"/>, so a
    /// teardown that observed an ambient token threw straight out of this method. The slot is still safe —
    /// the <c>finally</c> returns it before the throw propagates — but this method is called from the
    /// orchestrator's TERMINAL <c>finally</c>, and an exception raised there REPLACES the one already in
    /// flight. The run's actual failure is then lost and every log and retry decision downstream reads a
    /// cancellation that only ever described the teardown. A best-effort teardown must not be able to
    /// rewrite the run's cause of death, whatever it happens to throw.
    /// </summary>
    [Fact]
    public async Task ReleaseReviewLease_absorbs_a_cancelled_teardown_instead_of_masking_the_runs_failure()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        fixture.Provisioner.DestroyFailure = new OperationCanceledException("the gateway call was cancelled");

        // CancellationToken.None: nothing HERE is cancelled. The OCE is the teardown's own, raised against a
        // token this method never passed it — which is exactly why it must not read as "the caller cancelled".
        var act = async () => await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);

        await act.Should().NotThrowAsync();
        fixture.Pool.ReturnCount.Should().Be(1);
        fixture
            .CleanupOrder.Should()
            .ContainInOrder(
                ["destroy", "return"],
                "the teardown is still attempted first; only its failure is contained"
            );
    }

    /// <summary>
    /// Issue #479 item 1 — the seam test above pins that the release ABSORBS a cancelled teardown, but it
    /// puts no run failure in flight, so the MASK half of its name is unproven by it: a method that never
    /// throws cannot mask anything, and a method that throws only when something else already failed is the
    /// case that matters. This drives the real thing through the orchestrator: a stage fails, and the
    /// terminal <c>finally</c> then runs a teardown that is cancelled. The exception that reaches the caller
    /// — the one every log line and retry decision downstream reads — must be the STAGE's, not the
    /// teardown's.
    /// </summary>
    [Fact]
    public async Task Orchestrator_surfaces_the_stage_failure_when_the_terminal_teardown_is_cancelled()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // ContextReady (delegated to the real executor) leases a slot; Reviewed throws; the orchestrator's
        // terminal finally then calls ReleaseReviewLeaseAsync, whose teardown is cancelled.
        var executor = new ThrowAfterStageExecutor(fixture.Executor, throwAt: ReviewStage.Reviewed);
        var orchestrator = new PrOrchestrator(fixture.Store, executor, NullLogger<PrOrchestrator>.Instance);
        fixture.Provisioner.DestroyFailure = new OperationCanceledException("the gateway call was cancelled");

        var act = () => orchestrator.RunAsync(run, CancellationToken.None);

        (
            await act.Should()
                .ThrowAsync<InvalidOperationException>(
                    "the run died at Reviewed; a cancelled cleanup must not rewrite its cause of death"
                )
        ).WithMessage("*Simulated failure at stage Reviewed*");
        fixture
            .CleanupOrder.Should()
            .ContainInOrder(["destroy", "return"], "the teardown still ran, and its failure did not cost the slot");
        fixture.Pool.ReturnCount.Should().Be(1);
    }

    /// <summary>
    /// The containment must not cost idempotency: the atomic removal is what stops a concurrent release (or
    /// the Posted stage) from double-returning the slot, and the pool's semaphore throws on an over-return.
    /// </summary>
    [Fact]
    public async Task ReleaseReviewLease_after_a_thrown_teardown_still_does_not_double_return()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        fixture.Provisioner.DestroyFailure = new InvalidOperationException("gateway refused the unlink");

        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);

        fixture.Pool.ReturnCount.Should().Be(1, "the lease was already released; a second release is a no-op");
    }

    [Fact]
    public async Task ReleaseReviewLease_returns_the_leased_slot_and_is_idempotent()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // ContextReady leases a slot and holds it (for the review + commit-notes + terminal return).
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(0);

        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(1);
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);

        // Idempotent: a second release (e.g. the Posted stage already returned it) is a no-op, so the slot
        // is never double-returned to the pool.
        await fixture.Executor.ReleaseReviewLeaseAsync(run.Id, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(1, "the lease was already removed, so a second release is a no-op");
    }

    [Fact]
    public async Task Orchestrator_returns_the_leased_slot_when_a_stage_throws_after_ContextReady_leased()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // ContextReady (delegated to the real executor) leases a slot; a later stage then throws, so the run
        // never reaches Posted. Only the orchestrator's terminal finally can return the slot.
        var executor = new ThrowAfterStageExecutor(fixture.Executor, throwAt: ReviewStage.Reviewed);
        var orchestrator = new PrOrchestrator(fixture.Store, executor, NullLogger<PrOrchestrator>.Instance);

        var act = () => orchestrator.RunAsync(run, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.Pool.LeaseCount.Should().Be(1, "ContextReady leased a slot");
        fixture
            .Pool.ReturnCount.Should()
            .Be(1, "the orchestrator's terminal finally returned the slot despite the failure");
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task Orchestrator_returns_the_leased_slot_when_the_pr_is_no_longer_open()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        // A slot is leased (ContextReady) and then the PR is observed closed on the next poll, so RunAsync
        // short-circuits to Completed WITHOUT running the Posted stage that would normally return it.
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        fixture.Pool.ReturnCount.Should().Be(0);

        var orchestrator = new PrOrchestrator(fixture.Store, fixture.Executor, NullLogger<PrOrchestrator>.Instance);
        var closed = run with { PrLifecycleState = PrLifecycleState.Merged };

        var result = await orchestrator.RunAsync(closed, CancellationToken.None);

        result.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        fixture.Pool.ReturnCount.Should().Be(1, "the short-circuit finally returned the held slot");
        fixture.Pool.Returned.Should().ContainSingle(s => s.Index == 0);
    }

    [Fact]
    public async Task Posted_keeps_the_pooled_lease_when_the_commit_gate_fails_so_the_retry_uses_the_same_pool_path()
    {
        using var fixture = Fixture.Create();
        // The commit gate fails once (a stale index.lock the next attempt's clean-on-entry clears) and then
        // succeeds on the retry.
        fixture.HostRunner.OnArgvContainsSequence(
            $"add -- {NotesRelPath}",
            new SandboxCommandResult(1, string.Empty, "fatal: Unable to create index.lock: File exists"),
            new SandboxCommandResult(0, string.Empty, string.Empty)
        );
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Consuming the lease BEFORE the commit succeeded is what silently moved the retry off the pool and
        // onto the host ReviewBot checkout — so a failed retention must leave the lease exactly as it was.
        fixture.Pool.ReturnCount.Should().Be(0, "the slot is only stripped and returned once its notes are retained");

        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        var commands = fixture.HostRunner.Commands.Select(Describe).ToList();
        commands.Count(a => a.Contains($"add -- {NotesRelPath}")).Should().Be(2, "the retry re-runs the commit gate");
        commands
            .Should()
            .OnlyContain(
                a => !a.Contains($"add -- {NotesRelPath}") || a.Contains("/pool/slot-0/store"),
                "both attempts stage the notes inside the SAME leased slot"
            );
        fixture.Pool.LeaseCount.Should().Be(1, "the retry reuses the retained lease rather than leasing a second slot");
        fixture.Pool.ReturnCount.Should().Be(1, "the slot is returned exactly once, on the successful retry");
    }

    [Fact]
    public async Task Posted_re_leases_a_slot_when_a_retry_resumes_after_the_lease_was_released()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);

        // The orchestrator's terminal finally releases the pooled lease on EVERY terminal outcome (including
        // the failure→RetryPending rethrow), so a Posted-stage retry on a later poll — or after a restart —
        // always arrives with no recorded lease. Seed the gitmodules for the slot it will lease next.
        fixture.HostFileSystem.Seed(
            "/pool/slot-1/store/.gitmodules",
            "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n"
        );
        var resumed = fixture.BuildExecutor();

        await resumed.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        // The retry must retain the notes through the POOL — the store checkout that carries the notes branch
        // and the PR's prior notes — never silently degrade to the host ReviewBot checkout.
        fixture
            .Pool.LeaseCount.Should()
            .Be(2, "the resumed Posted stage re-leases a slot because the prior lease was released");
        var commands = fixture.HostRunner.Commands.Select(Describe).ToList();
        commands.Should().Contain(a => a.Contains($"add -- {NotesRelPath}") && a.Contains("/pool/slot-1/store"));
        fixture.Pool.ReturnCount.Should().Be(1, "the re-leased slot is stripped and returned on the terminal stage");
    }

    /// <summary>
    /// Issue #479 item 2 — the sibling exit of the predicate the release path's catch closed. The Posted
    /// stage tears the session down too, and there the teardown is uncaught: an
    /// <see cref="OperationCanceledException"/> from it failed a stage whose externally visible work — the
    /// review comment on the PR — had ALREADY landed, sending the run back to RetryPending to re-run a
    /// terminal stage that had nothing left to do. It also skipped the retention and the slot return below.
    /// A best-effort cleanup must not be able to undo a delivered review.
    /// </summary>
    [Fact]
    public async Task Posted_completes_and_returns_the_slot_when_the_session_teardown_is_cancelled()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        fixture.Provisioner.DestroyFailure = new OperationCanceledException("the gateway call was cancelled");

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);

        await act.Should().NotThrowAsync("the review is already delivered; a cancelled teardown cannot undo it");
        fixture.CleanupOrder.Should().Contain("destroy", "the teardown is still attempted, only contained");
        fixture
            .Pool.ReturnCount.Should()
            .Be(1, "retention and the slot return sit BELOW the teardown and must still run");
    }

    [Fact]
    public async Task Posted_strips_the_slot_store_to_pristine_after_committing_the_notes()
    {
        using var fixture = Fixture.Create();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // Commit-then-strip: after the notes are committed, the store working tree is reset + cleaned so the
        // next lease starts clean with nothing left around (the user's durability requirement).
        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands.Should().Contain(a => a.Contains("reset --hard"), "the slot store is reset on terminal return");
        commands.Should().Contain(a => a.Contains("clean -ffdx"), "untracked review byproduct is cleaned on return");
        fixture.Pool.ReturnCount.Should().Be(1, "the slot is still returned after the strip");
    }

    [Fact]
    public async Task Posted_skips_the_strip_on_S2S_because_the_slot_is_still_mounted_into_a_live_container()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // The strip is only safe once the session teardown has made the store quiescent, and that teardown is
        // excluded on S2S by design: the container belongs to the review host and must outlive the run so the
        // posted comment's deep link keeps working. StripAsync opens by deleting every *.lock under .git, so
        // running it here would delete a live index.lock and admit a SECOND writer — the hygiene function
        // becoming the very race it exists to prevent. Skipping leaves the store dirty until its next lease,
        // which is what clean-on-entry is for and what the call site's catch already tolerates.
        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands
            .Should()
            .NotContain(
                a => a.Contains("reset --hard"),
                "the store is not reset while a live container still has it mounted"
            );
        commands
            .Should()
            .NotContain(
                a => a.Contains("clean -ffdx"),
                "cleaning would wipe the checkout under a deep-link visitor mid-session"
            );
        fixture
            .Pool.ReturnCount.Should()
            .Be(1, "skipping the strip must never cost the slot its return, which would leak pool capacity");
    }

    [Fact]
    public async Task S2S_review_has_no_daemon_tool_context_yet_still_scopes_the_prompt_to_the_pooled_notes_dir()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The pooled path is tried FIRST on S2S too: the slot carries the store, the Knowledge Base and the
        // PR's own notes dir, so it is the richer workspace to mount into the hosted conversation.
        fixture.Pool.LeaseCount.Should().Be(1);
        fixture
            .Factory.ToolContexts.Should()
            .ContainSingle()
            .Which.Should()
            .BeNull("the hosted conversation owns its tools, so the daemon builds no tool context on S2S");

        // The regression guard: notes_dir/has_notes/has_store come from the pooled WRITE SCOPE, not from the
        // tool context. Sourcing them from the (null) tool context would render them empty HERE and silently
        // strip per-PR notes, re-review memory and the "only writable location" directive from the hosted
        // review — the review would still run and still look fine, which is why it needs pinning.
        var profile = fixture.Factory.CreatedProfiles.Should().ContainSingle().Subject;
        profile.SystemPrompt.Should().Contain($"/workspace/store/{NotesRelPath}");
        profile.SystemPrompt.Should().Contain("cross-repo store at /workspace/store");
        profile.SystemPrompt.Should().Contain($"/workspace/store/{SubmoduleRelPath}");
        profile.SystemPrompt.Should().MatchRegex("(?i)only writable location");
    }

    [Fact]
    public async Task S2S_binds_the_hosted_conversation_to_the_leased_slots_own_workspace()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The central design move: the leased slot IS the workspace LmStreaming mounts at /workspace, so every
        // container path the pooled stage computed (/workspace/store/...) is correct verbatim inside the hosted
        // conversation. Preparing a separate per-PR clone instead would mount a tree with no store at all.
        fixture.Factory.WorkspaceIds.Should().ContainSingle().Which.Should().Be("ws-review-slot-0");
        var created = fixture.S2SHandler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        created
            .Body.Should()
            .Contain(
                "\"directoryRelPath\":\"review-slot-0\"",
                "the workspace names the slot ROOT leaf, not a child of it"
            );
        fixture
            .S2SGit.Commands.Should()
            .BeEmpty("adoption is pure naming — re-running git here would fight the pool's preparer for the same tree");
    }

    [Fact]
    public async Task S2S_fails_closed_when_the_pooled_store_does_not_carry_the_reviewed_repo()
    {
        using var fixture = Fixture.CreateS2S();
        // The store declares a DIFFERENT submodule, so the pooled attempt DECLINES. On S2S the degrade below it
        // host-clones a permanent per-PR checkout under the shared gateway base and mints a workspace pointing
        // at it — neither of which anything ever reclaims, so every un-onboarded repo leaks a full clone plus a
        // workspace record. A configured pool that declines must fail closed instead, with an actionable error.
        fixture.HostFileSystem.Files.Clear();
        fixture.HostFileSystem.Seed(
            "/pool/review-slot-0/store/.gitmodules",
            "[submodule \"other\"]\n\tpath = repos/other\n\turl = https://github.com/achieveai/other.git\n"
        );
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("achieveai/lmdotnettools", "the error names the repo that must be onboarded");
        thrown.Message.Should().Contain(StoreUrl, "the error names the review store to onboard it into");
        fixture
            .S2SGit.Commands.Should()
            .BeEmpty("no unmanaged per-PR clone may be created for a pooled-but-declined review");
        fixture
            .S2SHandler.Requests.Should()
            .BeEmpty("no permanent per-PR LmStreaming workspace may be minted for a pooled-but-declined review");
        fixture.Pool.ReturnCount.Should().Be(1, "the declined lease is still returned normally, before the failure");
        fixture.Store.GetArtifacts(run.Id).Should().BeEmpty("the stage failed, so it persisted no partial context");
    }

    /// <summary>
    /// PR #230 follow-up: an operator can turn on <c>UseS2SReviewAgent</c> (which unconditionally wires
    /// <see cref="S2SReviewWorkspacePreparer"/> in Program.cs) without ever satisfying the pool-onboarding
    /// conditions (<c>EnableToolAssistedReview</c> + <c>EnableReviewerWrites</c> + a resolved review store) —
    /// so <see cref="DaemonReviewStageExecutor"/>'s <c>UsePooledReview</c> is <c>false</c> while the preparer is
    /// still non-null. Before this fix that combination fell through to the S2S "degrade" path and called
    /// <c>S2SReviewWorkspacePreparer.PrepareAsync</c> — a bare per-PR HOST CLONE plus a PERMANENT LmStreaming
    /// workspace REST record that nothing in this system ever cleans up. That is strictly worse than the
    /// pooled-but-declined case above (which at least fails closed): here there was no pool to decline, so the
    /// unmanaged clone+workspace was minted on every single S2S review of every PR. The fix rejects the review
    /// instead, before any preparer call, REST request, or host git — the same "fail closed rather than leak an
    /// unmanaged workspace" posture as the pooled-decline case, just for the "no pool configured at all" cause.
    /// </summary>
    [Fact]
    public async Task S2S_rejects_the_review_when_no_pooled_workspace_is_configured()
    {
        using var fixture = Fixture.CreateS2SWithoutPool();
        var run = fixture.SeedRun();

        var act = () => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("EnableToolAssistedReview", "the error names the flag that must be turned on");
        thrown
            .Message.Should()
            .Contain("EnableReviewerWrites", "the error names the other flag that must be turned on");
        thrown
            .Message.Should()
            .MatchRegex(
                "(?i)review store|pool",
                "the error points at onboarding a review store/pool, not just the flags"
            );
        fixture
            .S2SGit.Commands.Should()
            .BeEmpty("no unmanaged per-PR host clone may be created when no recyclable pooled workspace is configured");
        fixture
            .S2SHandler.Requests.Should()
            .BeEmpty(
                "no permanent per-PR LmStreaming workspace may be minted when no recyclable pooled workspace is configured"
            );
        fixture.Pool.LeaseCount.Should().Be(0, "no pool is configured at all, so nothing is ever leased");
        fixture.Store.GetArtifacts(run.Id).Should().BeEmpty("the stage failed, so it persisted no partial context");
    }

    [Fact]
    public async Task S2S_posts_host_side_with_the_deep_link_once_and_still_commits_only_the_notes_dir()
    {
        using var fixture = Fixture.CreateS2S();
        var run = fixture.SeedRun();

        await RunAllStagesAsync(fixture, run);

        // Agent-inline posting is forced OFF on S2S (the hosted agent is domain-agnostic and cannot reach a
        // GitHub/ADO PR) even though posting is authorized — so the synthesis turn, the one turn that would
        // otherwise carry the posting instructions, carries none…
        var inputs = fixture.Factory.CreatedAgents.Should().ContainSingle().Subject.ReceivedInputs;
        inputs.Should().HaveCount(2, "one hosted conversation still drives the provisional turn then synthesis");
        inputs[1]
            .Messages.OfType<TextMessage>()
            .Single()
            .Text.Should()
            .NotContain("api.github.com", "S2S must never ask the hosted agent to post to the PR itself");

        // …and the host-side publisher is the ONLY delivery path, carrying the deep-link back to the hosted
        // conversation (the whole point of the S2S path: a human can open the review and its sub-agent tree).
        fixture.Publisher.PostCount.Should().Be(1);
        var body = fixture.Publisher.PostedBodies.Should().ContainSingle().Subject;
        body.Split(S2SDeepLink(run), StringSplitOptions.None)
            .Length.Should()
            .Be(2, "the deep link is appended exactly once — a duplicated link means the body was assembled twice");
        body.Should()
            .NotContain(
                $"threadId=review-run-{run.Id}",
                "the link carries the id LmStreaming minted, not the daemon's own thread id (which resolves to nothing)"
            );

        // The commit gate is unchanged by S2S: still ONLY the PR notes dir, never `add -A`.
        var commands = fixture.HostRunner.Commands.Select(Join).ToList();
        commands.Should().Contain(a => a.Contains($"add -- {NotesRelPath}"));
        commands.Should().NotContain(a => a.Contains("add -A"));
        fixture.HostFileSystem.Writes.Should().Contain(p => p.Contains($"/{NotesRelPath}/") && p.EndsWith("review.md"));
        fixture.Pool.ReturnCount.Should().Be(1, "the slot is returned on the terminal stage on S2S too");
    }

    /// <summary>
    /// G15 — the isolation gate. This is the whole point of mounting a leased SLOT as the LmStreaming
    /// workspace leaf: two reviews that overlap in time must get two slots, two single-segment leaves, two
    /// LmStreaming workspaces (⇒ two gateway containers, since sessions are cached by workspace+app) and two
    /// notes dirs — and neither one's commit/strip may reach into the other's tree.
    /// <para>
    /// The poller is deliberately still serial, so nothing in production drives this today. The test is what
    /// makes flipping it to parallel a change in the POLLER ALONE: if the executor ever grew per-daemon shared
    /// review state, this fails instead of two live reviews silently corrupting each other's checkout.
    /// </para>
    /// </summary>
    [Fact]
    public async Task S2S_two_overlapping_reviews_get_isolated_slots_workspaces_and_notes_dirs()
    {
        using var fixture = Fixture.CreateS2S(slots: 2);
        var first = fixture.SeedRun("118");
        var second = fixture.SeedRun("222");

        // Hold BOTH preparations open until each has claimed its slot, so the overlap is deterministic: with a
        // plain WhenAll the first review could finish its context stage before the second even starts, and the
        // test would "pass" while proving nothing about two live leases.
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        fixture.Preparer.Rendezvous = () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothArrived.TrySetResult();
            }

            // A 30s ceiling so a regression that stops the second review from ever leasing fails loudly here
            // instead of hanging the suite.
            return bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        await Task.WhenAll(
            Task.Run(() => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, first, CancellationToken.None)),
            Task.Run(() => fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, second, CancellationToken.None))
        );

        // Both leases were held AT THE SAME TIME (the rendezvous could not have completed otherwise), and the
        // pool handed out two different slots rather than recycling one.
        fixture.Pool.LeaseCount.Should().Be(2);
        fixture
            .Pool.ReturnCount.Should()
            .Be(0, "both slots are still held — neither review has reached a terminal stage");
        // Typed locals rather than inline collection expressions: BeEquivalentTo's element type is a generic
        // parameter, which a target-typeless `[...]` cannot infer.
        string[] expectedSlots = ["/pool/review-slot-0", "/pool/review-slot-1"];
        fixture.Pool.Leased.Select(s => s.HostPath).Should().BeEquivalentTo(expectedSlots);

        // Two prepared checkouts in two different stores, and two different notes dirs.
        fixture.Preparer.Prepared.Select(p => p.StoreRoot).Should().OnlyHaveUniqueItems();
        string[] expectedNotesDirs =
        [
            $"/pool/review-slot-{SlotOf(fixture, "118")}/store/PRs/lmdotnettools-118",
            $"/pool/review-slot-{SlotOf(fixture, "222")}/store/PRs/lmdotnettools-222",
        ];
        fixture.Preparer.Prepared.Select(p => p.NotesDir).Should().BeEquivalentTo(expectedNotesDirs);

        // The rest runs sequentially — a serial poller is decision 2, and the review/judge/post stages share
        // fakes (agent factory, publisher) whose call ORDER these assertions read.
        fixture.Preparer.Rendezvous = null;
        await RunRemainingStagesAsync(fixture, first);
        await RunRemainingStagesAsync(fixture, second);

        // Two distinct LmStreaming workspaces, each named after its own slot leaf. Same workspace id for both
        // would mean ONE gateway container serving both reviews — the exact collision this design prevents.
        string[] expectedWorkspaceIds = ["ws-review-slot-0", "ws-review-slot-1"];
        fixture.Factory.WorkspaceIds.Distinct().Should().BeEquivalentTo(expectedWorkspaceIds);

        // The commit gate and the strip stay inside their own slot: every git command that names a PR's notes
        // dir must carry that PR's slot path, and no command may name one PR's notes under the other's slot.
        // Read BOTH fields: the notes-branch commands (checkout/add/commit/push) are scoped by
        // SandboxCommand.WorkingDirectory — only the target-dir reads and the strip pass `-C <path>` in argv —
        // so an argv-only projection would silently see no slot at all on exactly the commands under test.
        var commands = fixture.HostRunner.Commands.Select(Describe).ToList();
        foreach (var prId in new[] { "118", "222" })
        {
            var own = $"/pool/review-slot-{SlotOf(fixture, prId)}";
            var other = $"/pool/review-slot-{SlotOf(fixture, prId == "118" ? "222" : "118")}";
            commands
                .Should()
                .Contain(
                    a => a.Contains($"add -- PRs/lmdotnettools-{prId}") && a.Contains(own),
                    $"PR {prId}'s notes are staged in its own slot"
                );
            commands
                .Should()
                .NotContain(
                    a => a.Contains($"lmdotnettools-{prId}") && a.Contains(other),
                    $"nothing touching PR {prId} may reach into the other review's slot"
                );
        }

        // Neither slot is stripped: this is the S2S path, where the session teardown that would make a store
        // quiescent is excluded by design and the slot stays mounted into a live review-host container. The
        // isolation this test is about does not rest on the strip anyway — it rests on the two reviews holding
        // two different slots, which the assertions above establish directly. Byproduct left behind is cleared
        // by the next lease's clean-on-entry, the durability guarantee the strip was only ever a tidy-up for.
        foreach (var slot in new[] { "/pool/review-slot-0/store", "/pool/review-slot-1/store" })
        {
            commands.Should().NotContain(a => a.Contains($"-C {slot} reset --hard"));
            commands.Should().NotContain(a => a.Contains($"-C {slot} clean -ffdx"));
        }

        fixture.Pool.ReturnCount.Should().Be(2, "both slots are returned once their reviews reach Posted");
    }

    /// <summary>The slot index the pool leased for <paramref name="prId"/> — the assignment is whichever lease
    /// won the race, so the isolation assertions resolve it instead of assuming an order.</summary>
    private static int SlotOf(Fixture fixture, string prId)
    {
        var notesSuffix = $"/PRs/lmdotnettools-{prId}";
        var prepared = fixture.Preparer.Prepared.Single(p =>
            p.NotesDir.EndsWith(notesSuffix, StringComparison.Ordinal)
        );
        return fixture.Pool.Leased.Single(s => prepared.StoreRoot == s.StorePath).Index;
    }

    private static string Join(SandboxCommand command) => string.Join(' ', command.Argv);

    /// <summary>
    /// Argv prefixed with the directory the command runs in. A sandbox git command carries its repo either
    /// as <c>-C &lt;path&gt;</c> in argv or as <see cref="SandboxCommand.WorkingDirectory"/>; assertions about
    /// WHERE a command ran must therefore see both.
    /// </summary>
    private static string Describe(SandboxCommand command) => $"{command.WorkingDirectory} {Join(command)}";

    private static async Task RunAllStagesAsync(Fixture fixture, ReviewRun run)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await RunRemainingStagesAsync(fixture, run);
    }

    private static async Task RunRemainingStagesAsync(Fixture fixture, ReviewRun run)
    {
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await fixture.Executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _db;
        private readonly CodeReviewDaemonOptions _options;
        private readonly ReviewSlotWorkspace? _slotWorkspace;
        private readonly HttpClient? _s2sHttp;
        private readonly S2SReviewWorkspacePreparer? _s2sPreparer;

        /// <param name="s2s">Whether the review runs over the LmStreaming S2S API (wires the preparer) or
        /// in-process.</param>
        /// <param name="slots">How many slot leaves the fake pool is primed with.</param>
        /// <param name="wirePool">Mirrors Program.cs's SEPARATE pool-onboarding gate (EnableToolAssistedReview +
        /// EnableReviewerWrites + a resolved review store): when false, no <see cref="ReviewSlotWorkspace"/> is
        /// built at all, so <c>UsePooledReview</c> is false even though the S2S preparer (below) is still wired —
        /// exactly the "UseS2SReviewAgent on, pool never onboarded" operator misconfiguration PR #230 closes.</param>
        /// <param name="judge">Turns on the judge stage (off by default, as in production), so a test can drive
        /// <c>Judged</c> as a stage that does real work rather than one that returns immediately.</param>
        private Fixture(bool s2s, int slots, bool wirePool = true, bool judge = false)
        {
            _db = new TempSqliteDatabase();
            Store = new ReviewStore(_db.ConnectionString);
            BootRunner = new FakeSandboxCommandRunner()
                .OnArgvContains(
                    "rev-parse --is-inside-work-tree",
                    new SandboxCommandResult(1, string.Empty, "not a git repo")
                )
                .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty));
            HostRunner = new FakeSandboxCommandRunner().OnArgvContains(
                "diff",
                new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty)
            );
            // On S2S the slot dir doubles as the LmStreaming workspace leaf, so the pool is configured with the
            // single-segment "review-slot-" prefix Program.cs forces there (a "review-pool/slot-0" style name
            // would be FLATTENED by the workspace-directory sanitizer into a different, empty directory).
            var slotPrefix = s2s ? "review-slot-" : "slot-";
            HostFileSystem = new FakeSandboxFileSystem();
            for (var i = 0; i < slots; i++)
            {
                _ = HostFileSystem.Seed(
                    $"/pool/{slotPrefix}{i}/store/.gitmodules",
                    "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n\turl = https://github.com/achieveai/LmDotnetTools.git\n"
                );
            }

            Pool = new FakeReviewSlotPool("/pool", slotPrefix);
            Preparer = new FakeReviewSlotPreparer();

            // Shared cleanup-order log so a test can assert the session is destroyed before the slot is returned.
            Pool.Order = CleanupOrder;
            Provisioner.Order = CleanupOrder;

            _options = new CodeReviewDaemonOptions
            {
                EnableToolAssistedReview = wirePool,
                EnableReviewerWrites = wirePool,
                CrossRepoStoreUrl = wirePool ? StoreUrl : null,
                UseS2SReviewAgent = s2s,
                LmStreamingBaseUrl = s2s ? LmStreamingBaseUrl : null,
                // Host-side posting is the ONLY delivery path on S2S, so the S2S fixture authorizes it — that is
                // what makes the posted body (and its deep-link) observable on the fake publisher.
                EnableCommentPosting = s2s,
                // On for every pooled fixture, not just the feedback tests: the injection is inert unless the
                // run carries a sluggable PrAuthor (SeedRun leaves it null by default), so this changes nothing
                // for the other cases while keeping the flag from being the reason a real defect goes unseen.
                EnableReviewFeedbackAgent = true,
                EnableJudgeAgent = judge,
            };
            // Only the HOSTED path's turns are durable, and the executor now refuses an S2S review whose loop
            // cannot checkpoint them — so the double has to be resumable on exactly the path production is.
            Factory.Resumable = s2s;
            _slotWorkspace = wirePool
                ? new ReviewSlotWorkspace(
                    Pool,
                    Preparer,
                    (session, _) =>
                    {
                        // The production factory builds a preparer over the run session. The fake preparer records
                        // orchestration inputs; keep its SDK filesystem in sync with fixture-host seeds used by
                        // prior-notes/KB/root-guidance tests.
                        foreach (var (path, content) in HostFileSystem.Files)
                        {
                            var sessionPath = path.Replace(
                                $"/pool/{(s2s ? "review-slot-" : "slot-")}0/store",
                                "/workspace/store",
                                StringComparison.Ordinal
                            );
                            session
                                .FileSystem.WriteFileAsync(sessionPath, content, CancellationToken.None)
                                .GetAwaiter()
                                .GetResult();
                        }

                        return Preparer;
                    },
                    HostRunner,
                    HostFileSystem
                )
                : null;

            if (s2s)
            {
                // The REAL preparer over a scripted LmStreaming: the executor must ADOPT the leased slot (naming
                // it as the workspace, running no git) rather than host-cloning a bare per-PR checkout. The POST
                // ECHOES the leaf back as the workspace id ("ws-{leaf}") so a fixture with several slots hands
                // out a DISTINCT workspace per leaf — which is exactly what the isolation gate asserts.
                S2SHandler = new FakeHttpMessageHandler()
                    .OnJson(HttpMethod.Get, "api/workspaces", "[]")
                    .On(
                        req =>
                            req.Method == HttpMethod.Post
                            && req.RequestUri is not null
                            && req.RequestUri.ToString().Contains("api/workspaces", StringComparison.Ordinal),
                        req =>
                        {
                            var leaf = ReadDirectoryRelPath(req);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    $"{{\"id\":\"ws-{leaf}\",\"name\":\"Review {leaf}\",\"directoryRelPath\":\"{leaf}\","
                                        + "\"marketplaces\":[\"code-reviewer\"]}",
                                    Encoding.UTF8,
                                    "application/json"
                                ),
                            };
                        }
                    );

                _s2sHttp = new HttpClient(S2SHandler) { BaseAddress = new Uri(LmStreamingBaseUrl + "/") };
                _s2sPreparer = new S2SReviewWorkspacePreparer(
                    new LmStreamingS2SClient(_s2sHttp, "secret", "app-id", "app-key"),
                    new GitRunner(S2SGit),
                    "/pool",
                    reviewMarketplace: "code-reviewer",
                    NullLogger<S2SReviewWorkspacePreparer>.Instance
                );
            }

            Executor = BuildExecutor();
        }

        /// <summary>
        /// Builds an executor over the fixture's SHARED store/pool/preparer/provisioner. Each executor has its
        /// own in-memory <c>_leasedReviews</c>, so calling this a second time simulates a daemon RESTART: the
        /// persisted context artifact survives (shared store) while the process-local pooled lease does not.
        /// </summary>
        public DaemonReviewStageExecutor BuildExecutor() =>
            new(
                Store,
                Factory,
                BootRunner,
                BootFileSystem,
                _options,
                [Publisher],
                Logs,
                provisioner: Provisioner,
                slotWorkspace: _slotWorkspace,
                preparer: _s2sPreparer
            );

        public ReviewStore Store { get; }

        /// <summary>
        /// Everything the executor logged, shared by every executor this fixture builds (including the one a
        /// restart test builds second). Present because one guarantee here is about a line that must NOT be
        /// written: the uncomparable brief exists precisely so the degraded arm's "falling back to the inlined
        /// diff" warning — a false cause, promising a fallback onto a diff that does not exist — is never
        /// reached, and an absence cannot be asserted through a null sink.
        /// </summary>
        public CapturingLoggerFactory Logs { get; } = new();

        public FakeReviewAgentLoopFactory Factory { get; } = new();
        public FakeReviewCommentPublisher Publisher { get; } = new("github");
        public RecordingProvisioner Provisioner { get; } = new();
        public List<string> CleanupOrder { get; } = [];
        public FakeSandboxCommandRunner BootRunner { get; }
        public FakeSandboxCommandRunner HostRunner { get; }
        public FakeSandboxFileSystem HostFileSystem { get; }
        public FakeSandboxFileSystem BootFileSystem { get; } = new();
        public FakeReviewSlotPool Pool { get; }
        public FakeReviewSlotPreparer Preparer { get; }
        public DaemonReviewStageExecutor Executor { get; }

        /// <summary>The scripted LmStreaming S2S endpoint (S2S fixture only) — lets a test assert the workspace
        /// the daemon named to the review host.</summary>
        public FakeHttpMessageHandler S2SHandler { get; } = new();

        /// <summary>The git the S2S preparer runs through (S2S fixture only). Adoption must leave it EMPTY.</summary>
        public FakeSandboxCommandRunner S2SGit { get; } = new();

        public static Fixture Create(int slots = 1, bool judge = false) => new(s2s: false, slots, judge: judge);

        /// <summary>The S2S variant: the review runs in an LmStreaming-hosted conversation mounted over the
        /// leased slot, the daemon builds no tool context, and the Posted stage delivers the review host-side
        /// with the deep-link back to that conversation. <paramref name="slots"/> is how many slot leaves the
        /// fake pool is primed with — &gt;1 lets a test hold two leases at once.</summary>
        public static Fixture CreateS2S(int slots = 1, bool judge = false) => new(s2s: true, slots, judge: judge);

        /// <summary>The "explicit non-pooled S2S" variant (PR #230): <c>UseS2SReviewAgent</c> is on — so the
        /// S2S preparer is wired, mirroring Program.cs's unconditional registration — but none of the pool's
        /// own onboarding conditions are, so no <see cref="ReviewSlotWorkspace"/> exists and <c>UsePooledReview</c>
        /// is false. This is the misconfiguration that used to fall through to an unmanaged, never-cleaned-up
        /// per-PR host clone + LmStreaming workspace.</summary>
        public static Fixture CreateS2SWithoutPool() => new(s2s: true, slots: 1, wirePool: false);

        /// <summary>Reads <c>directoryRelPath</c> out of a create-workspace request body so the scripted
        /// endpoint can echo the leaf back as the workspace id.</summary>
        private static string ReadDirectoryRelPath(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(body ?? "{}");
            return json.RootElement.TryGetProperty("directoryRelPath", out var leaf)
                ? leaf.GetString() ?? "unknown"
                : "unknown";
        }

        /// <summary>
        /// Seeds (or resumes) a review run for <paramref name="prId"/>. Distinct PR ids give distinct runs —
        /// which is how the isolation gate drives two reviews at once.
        /// </summary>
        public ReviewRun SeedRun(string prId = "118", string? prAuthor = null, string mode = "collect-only")
        {
            var repoId = Store.EnsureRepo(
                new RepoIdentity
                {
                    Provider = "github",
                    OrgOrOwner = "achieveai",
                    RepoName = "LmDotnetTools",
                    RepoStableId = "repo-stable-1",
                }
            );
            return Store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = repoId,
                    PrId = prId,
                    PrAuthor = prAuthor,
                    HeadSha = "head-sha",
                    BaseSha = "base-sha",
                    TriggerWatermark = "wm-1",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = mode,
                    Stage = ReviewStage.Discovered,
                    WorkflowStatus = WorkflowStatus.Running,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );
        }

        public void Dispose()
        {
            _s2sHttp?.Dispose();
            Store.Dispose();
            _db.Dispose();
        }
    }

    /// <summary>Records lease/return calls and hands out forward-slash slot paths so the in-memory host
    /// file-system keys line up regardless of the OS path separator.</summary>
    private sealed class FakeReviewSlotPool : IReviewSlotPool
    {
        private readonly string _root;
        private readonly string _dirPrefix;
        private readonly Lock _gate = new();
        private int _next;

        /// <summary>
        /// <paramref name="dirPrefix"/> mirrors the real pool's slot-directory prefix: <c>slot-</c> in-process,
        /// <c>review-slot-</c> on S2S (where the slot dir doubles as the LmStreaming workspace leaf).
        /// </summary>
        public FakeReviewSlotPool(string root, string dirPrefix = "slot-")
        {
            _root = root;
            _dirPrefix = dirPrefix;
        }

        public int LeaseCount { get; private set; }
        public int ReturnCount { get; private set; }
        public int RetireCount { get; private set; }
        public int RecloneCount { get; private set; }
        public List<ReviewSlot> Returned { get; } = [];
        public List<ReviewSlot> Retired { get; } = [];

        /// <summary>Every slot handed out, in lease order — lets a test assert two concurrent reviews were
        /// never given the same slot.</summary>
        public List<ReviewSlot> Leased { get; } = [];

        /// <summary>Shared cleanup-order log (with <see cref="RecordingProvisioner"/>) to assert the session is
        /// destroyed before the slot is returned.</summary>
        public List<string>? Order { get; set; }

        public Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken)
        {
            // Gated because the isolation gate leases from two reviews at once: an unsynchronized index would
            // hand the SAME slot to both and manufacture the very collision the test exists to rule out.
            lock (_gate)
            {
                LeaseCount++;
                var index = _next++;
                var host = $"{_root}/{_dirPrefix}{index}";
                var slot = new ReviewSlot(index, host, $"{host}/store", $"{host}/scratch");
                Leased.Add(slot);
                return Task.FromResult(slot);
            }
        }

        public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ReturnCount++;
                Returned.Add(slot);
                Order?.Add("return");
            }

            return Task.CompletedTask;
        }

        public Task RetireAsync(ReviewSlot slot, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                RetireCount++;
                Retired.Add(slot);
                Order?.Add("retire");
            }

            return Task.CompletedTask;
        }

        public Task RecloneStoreAsync(ReviewSlot slot, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                RecloneCount++;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Records the prepare inputs and returns a <see cref="PreparedCheckout"/> whose paths are the
    /// forward-slash join of the slot store + the supplied relative paths (mirrors the real preparer).</summary>
    private sealed class FakeReviewSlotPreparer : IReviewSlotPreparer
    {
        private readonly Lock _gate = new();

        public int PrepareCount { get; private set; }
        public int RecloneCount { get; private set; }
        public string? LastSubmoduleRelPath { get; private set; }
        public string? LastBranch { get; private set; }
        public string? LastNotesRelPath { get; private set; }
        public string? LastDefaultBranch { get; private set; }

        /// <summary>Exceptions to throw on the first N prepare calls (then succeed) — drives the re-clone ladder.</summary>
        public Queue<Exception> ThrowThenSucceed { get; } = new();

        /// <summary>Every checkout handed back, in prepare order — the isolation gate asserts two concurrent
        /// reviews were prepared into two different slot stores and two different notes dirs.</summary>
        public List<PreparedCheckout> Prepared { get; } = [];

        /// <summary>
        /// What preparation "established" about <c>base...head</c>, stamped onto every checkout this fake
        /// hands back. Defaults to the same loud value production defaults to, so the tests that predate the
        /// merge-base outcome keep exercising the arm where a failed diff is simply a failure.
        /// </summary>
        public MergeBaseOutcome MergeBase { get; set; } = MergeBaseOutcome.Resolved;

        public Task EnsureStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecloneStoreAsync(string storeRoot, string storeUrl, CancellationToken cancellationToken)
        {
            RecloneCount++;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Optional rendezvous awaited AFTER the checkout is recorded but BEFORE prepare returns. The isolation
        /// gate uses it to hold both preparations open at once, so "two leases held simultaneously" is a
        /// property of the test rather than a scheduling accident that could pass on a lucky interleaving.
        /// </summary>
        public Func<Task>? Rendezvous { get; set; }

        public Task<PreparedCheckout> PrepareAsync(
            ReviewRun run,
            string storeRoot,
            string scratchRoot,
            string storeUrl,
            string submoduleRelPath,
            string branch,
            string defaultBranch,
            string notesRelPath,
            OperationPolicy policy,
            CancellationToken cancellationToken
        ) => PrepareCoreAsync(run, storeRoot, submoduleRelPath, branch, defaultBranch, notesRelPath, cancellationToken);

        public Task<PreparedCheckout> PrepareAsync(
            ReviewSlot slot,
            ReviewRun run,
            string storeUrl,
            string submoduleRelPath,
            string branch,
            string defaultBranch,
            string notesRelPath,
            OperationPolicy policy,
            CancellationToken cancellationToken
        ) =>
            PrepareCoreAsync(
                run,
                slot.StorePath,
                submoduleRelPath,
                branch,
                defaultBranch,
                notesRelPath,
                cancellationToken
            );

        private async Task<PreparedCheckout> PrepareCoreAsync(
            ReviewRun run,
            string storeRoot,
            string submoduleRelPath,
            string branch,
            string defaultBranch,
            string notesRelPath,
            CancellationToken cancellationToken
        )
        {
            PreparedCheckout checkout;
            lock (_gate)
            {
                PrepareCount++;
                if (ThrowThenSucceed.Count > 0)
                {
                    throw ThrowThenSucceed.Dequeue();
                }

                LastSubmoduleRelPath = submoduleRelPath;
                LastBranch = branch;
                LastNotesRelPath = notesRelPath;
                LastDefaultBranch = defaultBranch;
                checkout = new PreparedCheckout(
                    storeRoot,
                    $"{storeRoot}/{submoduleRelPath}",
                    $"{storeRoot}/{notesRelPath}",
                    branch,
                    MergeBase
                );
                Prepared.Add(checkout);
            }

            if (Rendezvous is { } rendezvous)
            {
                await rendezvous().ConfigureAwait(false);
            }

            return checkout;
        }
    }

    /// <summary>Hands back a session so the review stage can build a (scoped) tool context; the fake agent
    /// loop factory ignores the gateway details and just records the context it was given. Records which
    /// provisioning entry point the executor used (per-run vs slot-mount) and the slot it saw.</summary>
    private sealed class RecordingProvisioner : IReviewSessionProvisioner
    {
        public int GetOrCreateForSlotCalls { get; private set; }
        public int GetOrCreateCalls { get; private set; }
        public ReviewSlot? LastSlot { get; private set; }
        public FakeSandboxCommandRunner SdkRunner { get; } = new();
        public FakeSandboxFileSystem SdkFileSystem { get; } = new();

        /// <summary>
        /// What <c>git diff --name-only</c> answers in the session. Settable so a test can make it FAIL, which is
        /// how the changed-path listing goes missing on a live run — <c>BuildChangedPathsAsync</c> degrades to an
        /// empty listing on a non-zero exit rather than failing the run.
        /// </summary>
        public SandboxCommandResult NameOnlyResult { get; set; } = new(0, "Foo.cs\n", string.Empty);

        /// <summary>Shared cleanup-order log (with <see cref="FakeReviewSlotPool"/>).</summary>
        public List<string>? Order { get; set; }

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
        {
            GetOrCreateCalls++;
            return Task.FromResult<ReviewRunSession?>(
                new ReviewRunSession(
                    $"session-{run.Id}",
                    $"/workspace/review-run-{run.Id}",
                    new FakeSandboxCommandRunner(),
                    new FakeSandboxFileSystem()
                )
            );
        }

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)
        {
            GetOrCreateForSlotCalls++;
            LastSlot = slot;
            return Task.FromResult<ReviewRunSession?>(Session(run, slot));
        }

        public Task<ReviewRunSession> GetOrCreateRequiredForSlotAsync(
            ReviewRun run,
            ReviewSlot slot,
            CancellationToken ct
        )
        {
            GetOrCreateForSlotCalls++;
            LastSlot = slot;
            return Task.FromResult(Session(run, slot));
        }

        private ReviewRunSession Session(ReviewRun run, ReviewSlot slot)
        {
            SdkFileSystem.Seed(
                "/workspace/store/.gitmodules",
                "[submodule \"LmDotnetTools\"]\n\tpath = repos/LmDotnetTools\n"
                    + "\turl = https://github.com/achieveai/LmDotnetTools.git\n"
            );
            // Registered FIRST so it wins over the broader patch rule below: the two commands differ only by
            // flags, and a runner rule that matched the patch would otherwise answer the listing with a patch.
            SdkRunner.OnArgvContainsFirst("diff --name-only", NameOnlyResult);
            SdkRunner.OnArgvContains(
                "diff base-sha...head-sha",
                new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ x", string.Empty)
            );
            return new ReviewRunSession($"session-{run.Id}", slot.HostPath, SdkRunner, SdkFileSystem);
        }

        /// <summary>
        /// When set, every <c>DestroyAsync</c> throws it. Production's ReviewSessionProvisioner swallows its
        /// own failures, but the pooled slot's return must not DEPEND on that: the release path is the only
        /// thing that can give the slot back, and it must survive a teardown that throws.
        /// </summary>
        public Exception? DestroyFailure { get; set; }

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Destroy();

        public Task DestroyAsync(long runId, CancellationToken ct) => Destroy();

        private Task Destroy()
        {
            Order?.Add("destroy");
            return DestroyFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    /// <summary>Delegates every stage to a real executor but throws at a chosen stage, so a run driven
    /// through the orchestrator leases a slot in ContextReady and then fails before Posted — proving the
    /// slot is returned by the orchestrator's terminal <c>finally</c> (via the delegated
    /// <see cref="IReviewStageExecutor.ReleaseReviewLeaseAsync"/>), not by the Posted stage.</summary>
    private sealed class ThrowAfterStageExecutor : IReviewStageExecutor
    {
        private readonly IReviewStageExecutor _inner;
        private readonly ReviewStage _throwAt;

        public ThrowAfterStageExecutor(IReviewStageExecutor inner, ReviewStage throwAt)
        {
            _inner = inner;
            _throwAt = throwAt;
        }

        public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
        {
            if (stage == _throwAt)
            {
                throw new InvalidOperationException($"Simulated failure at stage {stage}.");
            }

            return _inner.ExecuteStageAsync(stage, run, cancellationToken);
        }

        public Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken) =>
            _inner.ReleaseReviewLeaseAsync(runId, cancellationToken);
    }
}
