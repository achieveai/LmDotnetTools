using System.Globalization;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// What the daemon writes into a PR's notes directory, and — just as importantly — what it refuses to write.
/// <para>
/// Everything under <c>PRs/&lt;slug&gt;-&lt;n&gt;/</c> is read back <b>whole</b>: the next round's
/// prior-notes input and the knowledge extractor's prompt are both built by concatenating every file in the
/// directory. That makes these artifacts a shared budget, not a private log, and it makes two properties
/// load-bearing: the reviewer's <i>conclusions</i> must survive, and its tool traffic must not. Both are
/// pinned here.
/// </para>
/// </summary>
public sealed class ReviewNotesArtifactBuilderTests
{
    private const string RootThread = "thread-root";

    /// <summary>
    /// A scripted stand-in for the review host. Records which route each caller took, so a test can prove
    /// the lead was read from the <b>root conversation</b> and not by naming an id in the descendant roster
    /// (it is not in that roster — that is the whole reason the second route exists).
    /// </summary>
    private sealed class FakeTranscripts : IReviewAgentTranscriptSource
    {
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry> _descendant;
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry>? _root;
        private readonly Exception? _rootFailure;

        public FakeTranscripts(
            IReadOnlyList<ReviewAgentTranscriptEntry> descendant,
            IReadOnlyList<ReviewAgentTranscriptEntry>? root = null,
            Exception? rootFailure = null)
        {
            _descendant = descendant;
            _root = root;
            _rootFailure = rootFailure;
        }

        public List<string> RequestedAgentIds { get; } = [];

        public int RootReads { get; private set; }

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
            string rootThreadId,
            string agentId,
            CancellationToken ct)
        {
            RequestedAgentIds.Add(agentId);
            return Task.FromResult(_descendant);
        }

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
            string rootThreadId,
            CancellationToken ct)
        {
            RootReads++;
            return _rootFailure is null
                ? Task.FromResult(_root ?? [])
                : Task.FromException<IReadOnlyList<ReviewAgentTranscriptEntry>>(_rootFailure);
        }
    }

    private static ReviewAgentTranscriptEntry Entry(string messageType, string body, string role = "assistant") =>
        new(messageType, role, FromAgent: null, TimestampUtc: null, Body: body);

    private static ReviewRun NewRun(string? promptTemplateHash = null) =>
        new()
        {
            RepoId = 1,
            PrId = "250",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Reviewed,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
            PromptTemplateHash = promptTemplateHash,
        };

    private static RepoIdentity NewRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };

    private static ReviewSubAgentNode Node(string agentId, string name) =>
        new()
        {
            AgentId = agentId,
            ThreadId = $"subagent-{agentId}",
            ParentThreadId = RootThread,
            Depth = 1,
            Status = ReviewSubAgentStatus.Completed,
            Name = name,
            Template = "reviewer",
        };

    private static ReviewNotesArtifactContext NewContext(params ReviewSubAgentNode[] nodes) =>
        new(
            ReviewRound: 1,
            ModelId: "test-model",
            ToolAssisted: true,
            HostedThreadId: RootThread,
            LocalThreadId: "local-thread",
            CheckoutRoot: "/checkout",
            StoreRoot: "/store",
            NotesDir: "/store/PRs/lmdotnettools-250",
            PrevHeadSha: null,
            Roster: new ReviewSubAgentTreeSnapshot(nodes));

    private static ReviewNotesArtifactBuilder NewBuilder(IReviewAgentTranscriptSource? transcripts) =>
        new(transcripts, NullLogger.Instance);

    private static Task<ReviewNotesArtifacts> BuildFullAsync(
        ReviewNotesArtifactBuilder builder,
        ReviewNotesArtifactContext context,
        string? shippedReviewBody = null) =>
        builder.BuildAsync(
            NewRun(), NewRepo(), "PRs/lmdotnettools-250", context, CancellationToken.None, shippedReviewBody);

    private static async Task<IReadOnlyList<ReviewArtifactFile>> BuildAsync(
        ReviewNotesArtifactBuilder builder,
        ReviewNotesArtifactContext context,
        string? shippedReviewBody = null) =>
        (await BuildFullAsync(builder, context, shippedReviewBody)).Files;

    private static ReviewArtifactFile Reconciliation(IReadOnlyList<ReviewArtifactFile> files) =>
        files.Single(f => f.RelativePath.EndsWith("PR_Reconciliation_01.md", StringComparison.Ordinal));

    [Fact]
    public async Task Tool_traffic_accounting_and_reasoning_are_dropped_and_the_omission_is_disclosed()
    {
        // One conclusion buried in the keystroke log of how it was reached. Only the conclusion is worth
        // carrying into the next round's context window.
        var transcripts = new FakeTranscripts(
        [
            Entry("ToolCallMessage", "{\"name\":\"Read\",\"path\":\"src/Foo.cs\"}"),
            Entry("ToolsCallResultMessage", "…4000 lines of file content…"),
            Entry("ToolsCallAggregateMessage", "grep results"),
            Entry("UsageMessage", "{\"inputTokens\":123}"),
            Entry("ReasoningMessage", "private deliberation"),
            Entry("TextMessage", "FINDING: null deref in Foo.Bar at line 42."),
        ]);
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Should().Contain("FINDING: null deref in Foo.Bar at line 42.");
        findings.Content.Should().NotContain("4000 lines of file content");
        findings.Content.Should().NotContain("private deliberation");
        findings.Content.Should().NotContain("inputTokens");
        // A silent filter would recreate the exact "quiet reviewer" failure this builder exists to end.
        findings.Content.Should().Contain("5 of 6 message(s) omitted");
    }

    [Fact]
    public async Task Empty_bodied_messages_do_not_become_empty_transcript_sections()
    {
        var transcripts = new FakeTranscripts(
        [
            Entry("TextMessage", "   "),
            Entry("TextMessage", "real content"),
        ]);
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "tests")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_tests", StringComparison.Ordinal));
        findings.Content.Should().Contain("real content");
        findings.Content.Should().Contain("1 of 2 message(s) omitted");
    }

    [Fact]
    public async Task An_agent_that_only_ran_tools_is_reported_as_such_rather_than_left_blank()
    {
        var transcripts = new FakeTranscripts([Entry("ToolCallMessage", "{\"name\":\"Grep\"}")]);
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "performance")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_performance", StringComparison.Ordinal));
        findings.Content.Should().Contain("produced no prose of its own");
    }

    [Fact]
    public async Task The_lead_reviewer_gets_its_own_file_at_index_00_read_from_the_root_conversation()
    {
        var transcripts = new FakeTranscripts(
            descendant: [Entry("TextMessage", "specialist says")],
            root: [Entry("TextMessage", "VERDICT: request changes — see finding 1.")]);
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var lead = files.Single(f => f.RelativePath.EndsWith("PR_Findings_01_00_lead-reviewer.md", StringComparison.Ordinal));
        lead.Content.Should().Contain("VERDICT: request changes");
        // The lead is not a node in the roster, so it must never be fetched by naming an agent id.
        transcripts.RootReads.Should().Be(1);
        transcripts.RequestedAgentIds.Should().Equal("agent-1");

        // Re-reviews bootstrap by concatenating files whose names start with PR_Context_ or PR_Findings_;
        // the lead file has to be inside that filter or the deciding voice is lost again next round.
        lead.RelativePath.Should().Contain("/PR_Findings_");
        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("PR_Findings_01_00_lead-reviewer.md");
    }

    [Fact]
    public async Task A_lead_transcript_the_host_will_not_serve_still_produces_a_file_that_says_so()
    {
        var transcripts = new FakeTranscripts(
            descendant: [Entry("TextMessage", "specialist says")],
            rootFailure: new InvalidOperationException("host returned 404"));
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var lead = files.Single(f => f.RelativePath.EndsWith("PR_Findings_01_00_lead-reviewer.md", StringComparison.Ordinal));
        lead.Content.Should().Contain("could not read this transcript");
        lead.Content.Should().Contain("host returned 404");
        // The gap is visible in the store's own manifest, not only in the daemon's log.
        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("transcript unavailable");

        // A read that FAILED and a read that succeeded and carried nothing are different problems with
        // different fixes, so they must never share a rendering. 11 live empty files are honest 404s and say
        // so; that posture is what works here and must not be diluted into the newer one.
        lead.Content.Should().NotContain("READ BUT EMPTY");
        contextFile.Content.Should().NotContain("none of this agent's own output survived");
    }

    [Fact]
    public async Task A_review_with_no_sub_agents_still_writes_the_lead_and_context_files()
    {
        var transcripts = new FakeTranscripts(
            descendant: [],
            root: [Entry("TextMessage", "reviewed alone; no specialists needed")]);
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext());

        files.Select(f => f.RelativePath).Should().BeEquivalentTo(
        [
            "PRs/lmdotnettools-250/PR_Context_01.md",
            "PRs/lmdotnettools-250/PR_Reconciliation_01.md",
            "PRs/lmdotnettools-250/PR_Findings_01_00_lead-reviewer.md",
        ]);
        files.Single(f => f.RelativePath.Contains("lead-reviewer", StringComparison.Ordinal))
            .Content.Should().Contain("no specialists needed");

        // The reconciliation file is deliberately OUTSIDE the PR_Context_/PR_Findings_ prefix the next round
        // reads back. It is an audit of this round, not input to the next one; inside the prefix it would put
        // every finding into the following review's context a second time.
        Reconciliation(files).RelativePath.Should().NotContain("/PR_Findings_")
            .And.NotContain("/PR_Context_");
    }

    [Fact]
    public async Task Transcript_volume_is_bounded_so_one_verbose_reviewer_cannot_eat_the_next_rounds_context()
    {
        // Retained prose, not tool traffic — the budget has to hold even when every message is legitimate.
        var entries = Enumerable.Range(0, 200)
            .Select(i => Entry("TextMessage", $"finding {i}: " + new string('x', 4_000)))
            .ToArray();
        var builder = NewBuilder(new FakeTranscripts(entries));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Length.Should().BeLessThan(
            30_000,
            "the whole notes directory is concatenated into the next round's prompt and the extractor's");
        findings.Content.Should().Contain("budget reached");
    }

    // ── A transcript read in full can still yield nothing, and nothing used to say so ─────────────────
    //
    // Three outcomes of a transcript read, and three renderings, because the fixes differ:
    //
    //   the host refused                → "could not read this transcript" + the error   (daemon-side gap)
    //   the host answered with nothing  → "returned no messages for this agent"          (quiet reviewer)
    //   the host answered, none of it   → READ BUT EMPTY                                 (missing output in a
    //   was the agent's own turn                                                          record that looks
    //                                                                                     complete)
    //
    // The third was reported as a success: TranscriptRead=true, no warning, a findings file that renders. Two
    // live specialist transcripts landed there — one with 78 of 79 messages filtered as tool traffic, one with
    // 256 of 259 — which is the failure this whole class exists to end, surviving inside it.

    [Fact]
    public async Task A_transcript_read_in_full_but_filtered_to_nothing_says_so_and_warns()
    {
        var logs = new CapturingLogger<object>();
        var builder = new ReviewNotesArtifactBuilder(
            new FakeTranscripts(
            [
                Entry("ToolCallMessage", "{\"name\":\"Grep\",\"pattern\":\"await\"}", role: "user"),
                Entry("ToolsCallResultMessage", "…900 matches…", role: "user"),
                // Survives the tool-traffic filter, and is still not one word this agent wrote.
                Entry("TextMessage", "REVIEW BRIEF: examine the telemetry module.", role: "user"),
            ]),
            logs);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "agentic-performance")));
        var findings = files.Single(f => f.RelativePath.Contains("_01_agentic-performance", StringComparison.Ordinal));

        // Rendering one: the file states the outcome, with the counts that make it checkable.
        findings.Content.Should().Contain("READ BUT EMPTY");
        findings.Content.Should().Contain("2 of 3 message(s) were filtered");
        findings.Content.Should().NotContain("could not read this transcript");
        findings.Content.Should().NotContain("returned no messages for this agent");

        // Rendering two: the manifest, so the gap is visible without opening the file — and distinct from the
        // wording a read failure gets.
        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("none of this agent's own output survived");
        contextFile.Content.Should().NotContain("transcript unavailable");

        // Rendering three: the operator signal, naming the agent, its template, and the counts. Exactly one —
        // the lead's own read returned no messages at all, which is the other state and is not this warning.
        logs.MessagesAtLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Should()
                .Contain("READ SUCCESSFULLY but yielded no")
                .And.Contain("agentic-performance")
                .And.Contain("reviewer")
                .And.Contain("2 of 3");
    }

    [Fact]
    public async Task A_host_that_returned_no_messages_is_not_reported_as_read_but_empty()
    {
        // The discriminator. A reviewer the host has nothing for is a different fact from a reviewer whose
        // own output was filtered away, and the second must not be able to hide inside the first.
        var logs = new CapturingLogger<object>();
        var builder = new ReviewNotesArtifactBuilder(new FakeTranscripts(descendant: []), logs);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "telemetry")));
        var findings = files.Single(f => f.RelativePath.Contains("_01_telemetry", StringComparison.Ordinal));

        findings.Content.Should().Contain("returned no messages for this agent");
        findings.Content.Should().NotContain("READ BUT EMPTY");
        logs.MessagesAtLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // ── What the shipped review did with each specialist finding ──────────────────────────────────────
    //
    // The specialists' findings are captured — one file per roster node, committed and pushed. What was
    // captured NOWHERE is what happened to each one. Traced by hand on one live 7-specialist run: six findings
    // survived intact, one was merged, and two were transformed invisibly — an architecture [BLOCKER] High at
    // a DI-coupling site shipped as MEDIUM reframed into a test gap, and a telemetry [MEDIUM] shipped as a
    // context question. Same file:line, different severity, different meaning, no record anywhere.

    private const string DiCouplingSite = "src/Workflow/WorkflowDistributedTasksModule.cs:42-45";

    [Fact]
    public async Task A_finding_the_review_shipped_at_a_different_severity_records_BOTH_severities()
    {
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "## Findings\n"
                    + "#### [BLOCKER] High — module resolves its own dependencies\n"
                    + "Problem: the module reaches into the container.\n"
                    + $"Location: {DiCouplingSite}\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody:
                "## Findings\n"
                + "#### [MEDIUM] no test covers the module wiring\n"
                + $"{DiCouplingSite} is untested.\n");

        // BOTH severities on one row, with the outcome between them. The shipped severity alone is the state
        // we already had — a review that says MEDIUM and no record that a specialist called it a blocker.
        Reconciliation(files).Content.Should().Contain("| Blocker/High | `severity-changed` | Medium |");
    }

    [Fact]
    public async Task A_finding_the_shipped_review_never_cites_is_still_on_the_record_as_dropped()
    {
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "#### [BLOCKER] High — unchecked cast\n"
                    + "src/Foo.cs:10 casts without a type test.\n"
                    + "\n"
                    + "#### [MEDIUM] exception ignored\n"
                    + "src/Telemetry/Exporter.cs:39-54 swallows the failure.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "telemetry")),
            shippedReviewBody:
                "#### [BLOCKER] High — unchecked cast\n"
                + "src/Foo.cs:10 must be guarded.\n");

        var reconciliation = Reconciliation(files).Content;

        // The row exists, names the location, and says what became of it. Omitting it would put us back
        // exactly where we started: a finding that leaves no trace of having been considered.
        reconciliation.Should().Contain("src/Telemetry/Exporter.cs:39-54");
        reconciliation.Should().Contain("| Medium | `dropped` |");
        reconciliation.Should().Contain("| `dropped` | 1 |");
        reconciliation.Should().Contain("| `kept` | 1 |");

        // And the file refuses to let that number be read as attrition, because it is not one.
        reconciliation.Should().Contain("is not a loss rate");
    }

    [Fact]
    public async Task A_finding_the_review_shipped_as_a_question_is_recorded_as_reframed()
    {
        // The live telemetry case: a [MEDIUM] exception-ignored finding came out under the review's context
        // questions. Same file:line, and nothing said the severity had stopped applying.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "#### [MEDIUM] exception swallowed\n"
                    + "src/Telemetry/Exporter.cs:39-54 discards the exception.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "telemetry")),
            shippedReviewBody:
                "## Context questions\n"
                + "#### [QUESTION] Is the discarded exception here deliberate?\n"
                + "src/Telemetry/Exporter.cs:39-54\n");

        Reconciliation(files).Content.Should().Contain("`reframed`");
    }

    [Fact]
    public async Task Two_specialists_landing_on_one_shipped_finding_are_recorded_as_merged()
    {
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [MEDIUM] duplicated guard\nsrc/Foo.cs:10 repeats the check.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture"), Node("agent-2", "duplication")),
            shippedReviewBody: "#### [MEDIUM] duplicated guard\nsrc/Foo.cs:10 repeats the check.\n");

        Reconciliation(files).Content.Should().Contain("| `merged-into` | 2 |");
    }

    [Fact]
    public async Task An_unexplained_demotion_is_recorded_as_unexplained_rather_than_given_a_reason()
    {
        // The hardest constraint on this file. Where the synthesis states no reason there must be none — an
        // invented rationale would read exactly like a recorded one, which is worse than the nothing we had.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", $"#### [BLOCKER] High — DI coupling\n{DiCouplingSite}\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: $"#### [LOW] wiring detail\n{DiCouplingSite}\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("| Blocker/High | `severity-changed` | Low |");
        // Trailing cell empty. The shipped review said nothing about why, so neither does this.
        reconciliation.Should().Contain("| Low | [LOW] wiring detail | — |");
        reconciliation.Should().Contain("The daemon does not supply one when the review gave none.");
        // …and the file must say a blank cell is the NORM here, not evidence the reviewer was silent.
        // Measured: reviewers state a disposition in 28 of 260 shipped reviews, but ~1 row in 250 can be
        // attributed one, because they write it in a review-level section rather than the finding's block.
        reconciliation.Should().Contain("A blank cell is normal here, and does not mean silence.");
    }

    [Fact]
    public async Task A_reason_the_shipped_review_actually_stated_is_quoted_verbatim()
    {
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", $"#### [BLOCKER] High — DI coupling\n{DiCouplingSite}\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody:
                "#### [MEDIUM] module wiring\n"
                + $"{DiCouplingSite}\n"
                + "Downgraded from blocker: the container registration is validated at startup.\n");

        Reconciliation(files).Content.Should()
            .Contain("Downgraded from blocker: the container registration is validated at startup.");
    }

    [Fact]
    public async Task Without_the_shipped_review_body_nothing_is_reported_as_lost()
    {
        // "Not compared" and "not carried" are different facts, and only one of them is a loss. A build with
        // no review body to compare against must say so, never emit a page of dropped rows.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [BLOCKER] High — unchecked cast\nsrc/Foo.cs:10\n"),
        ]));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("Not compared");
        reconciliation.Should().Contain("not** a report that findings were lost");
        reconciliation.Should().NotContain("`dropped`");
    }

    [Fact]
    public async Task The_reconciliation_table_is_bounded_but_its_totals_still_count_every_finding()
    {
        // This file is inside a shared budget twice over: the at-close knowledge extractor concatenates EVERY
        // file under the PR's notes dir with no prefix filter, so an unbounded table here is spent out of that
        // prompt's context window. The arithmetic is deliberately NOT bounded — a truncated table that also
        // truncated its own counts would be worse than no table.
        var specialist = string.Concat(
            Enumerable.Range(0, 200).Select(
                i => $"#### [MEDIUM] finding {i}\nsrc/Gen/File{i}.cs:{i + 1} is wrong.\n\n"));
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", specialist)]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "## Findings\n\nNothing worth reporting.\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Length.Should().BeLessThan(
            20_000,
            "the at-close extractor concatenates every file in this directory into one prompt");
        reconciliation.Should().Contain("further row(s) omitted");
        reconciliation.Should().Contain("| `dropped` | 200 |");
        reconciliation.Should().Contain("| **total** | 200 |");
    }

    // ── The matcher's two guards, each of which survived a mutation until pinned here ─────────────────
    //
    // Both clauses in CitationsMatch were correct and completely untested: deleting either one still compiled
    // and left the whole suite green. A guard nothing exercises is one refactor away from being deleted by
    // someone who cannot see what it was for, and the failure it prevents is SILENT — two unrelated findings
    // joined into one row that reads exactly like a real match.

    [Fact]
    public async Task A_path_that_merely_ends_with_another_is_not_the_same_file()
    {
        // `Foo.cs` is a suffix of `src/BarFoo.cs` as a STRING, and a different file. Without the
        // segment-boundary clause these join and the finding is reported as carried when it was not.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [BLOCKER] High — unchecked cast\nFoo.cs:10 casts without a type test.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "#### [BLOCKER] High — unchecked cast\nsrc/BarFoo.cs:10 casts without a type test.\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("| `dropped` | 1 |");
        reconciliation.Should().Contain(
            "| `kept` | 0 |",
            "a string-suffix collision must never be reported as the same location");
    }

    [Fact]
    public async Task The_same_file_at_a_line_the_review_never_mentions_is_not_a_match()
    {
        // Same path, disjoint lines. Without the range-overlap clause every finding in a file would join
        // every other finding in that file — the most permissive wrong answer available.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [BLOCKER] High — unchecked cast\nsrc/Foo.cs:10 casts without a test.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "#### [BLOCKER] High — missing dispose\nsrc/Foo.cs:200 leaks the handle.\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("| `dropped` | 1 |");
        reconciliation.Should().Contain(
            "| `kept` | 0 |",
            "two findings in one file at unrelated lines are not the same finding");
    }

    // ── The parse stage must not manufacture findings ─────────────────────────────────────────────────
    //
    // Run over 810 stored review texts, 4 of 6 spot-checked `dropped` rows were never findings at all:
    // `**3 HIGH/BLOCKER findings**` and `**1 MEDIUM finding**` are counts in a summary block, and one
    // `question` flag came from a prose sentence merely containing the word. For an artifact whose entire
    // purpose is a credible record of what was discarded, that noise is the defect that matters most.

    [Fact]
    public async Task A_count_of_findings_is_not_itself_a_finding()
    {
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "## Summary\n"
                + "- **3 HIGH/BLOCKER findings**\n"
                + "- **1 MEDIUM finding**\n"
                + "\n"
                + "## Findings\n"
                + "#### [BLOCKER] High — unchecked cast\n"
                + "src/Foo.cs:10 casts without a type test.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "#### [BLOCKER] High — unchecked cast\nsrc/Foo.cs:10 must be guarded.\n");

        var reconciliation = Reconciliation(files).Content;
        // Exactly the one real finding. The two tally bullets carry severity words and cite nothing, so
        // without the exclusion they arrive as two bogus `dropped` rows.
        reconciliation.Should().Contain("| **total** | 1 |");
        reconciliation.Should().Contain("| `dropped` | 0 |");
        // The bold form is the tally bullet's own text; the method prose quotes the example in backticks.
        reconciliation.Should().NotContain("**3 HIGH/BLOCKER findings**");
    }

    [Fact]
    public async Task The_word_question_in_a_sentence_does_not_make_a_question_item()
    {
        // The live shape: a severity-bearing lead line whose prose happens to use the word. Treating it as a
        // question marker both invented a finding and mislabelled its outcome.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "#### [MEDIUM] analyzer version-skew question remains unresolved\n"
                + "src/Foo.cs:10 pins an older analyzer.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "#### [MEDIUM] analyzer version skew\nsrc/Foo.cs:10 pins an older analyzer.\n");

        var reconciliation = Reconciliation(files).Content;
        // Medium on both sides, so this is `kept`. If the word made it a question the specialist severity
        // would read `Medium/Question` and the outcome would not be `kept`.
        reconciliation.Should().Contain("| Medium | `kept` | Medium |");
        reconciliation.Should().NotContain("Medium/Question");
    }

    [Fact]
    public async Task An_item_that_was_already_a_question_and_stayed_one_was_not_reframed()
    {
        // `reframed` exists to surface a finding that shipped as a question. Of 283 real rows exactly one
        // landed here and it was ALREADY a [QUESTION] in the source — so the entire observed population of
        // the label was the no-op case. Two different things must not share one label.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [QUESTION] Is the discarded exception deliberate?\nsrc/Foo.cs:10\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "telemetry")),
            shippedReviewBody:
                "## Context questions\n"
                + "#### [QUESTION] Is the discarded exception deliberate?\n"
                + "src/Foo.cs:10\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("| `reframed` | 0 |");
        reconciliation.Should().Contain("| `kept` | 1 |");
    }

    [Fact]
    public async Task Prose_about_the_codes_behaviour_is_not_quoted_as_an_editorial_reason()
    {
        // The live false positive: a bare `deduplicat` marker quoted "exact duplicate rows continue to
        // deduplicate." — a sentence about what the CODE does — as though it were a decision about this
        // finding. A reason column that is sometimes about something else is worse than a blank one.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [BLOCKER] High — duplicate rows\nsrc/Foo.cs:10 inserts twice.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody:
                "#### [MEDIUM] duplicate rows\n"
                + "src/Foo.cs:10 inserts twice.\n"
                + "- exact duplicate rows continue to deduplicate.\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("| Blocker/High | `severity-changed` | Medium |");
        reconciliation.Should().NotContain("continue to deduplicate");
        // Blank stays the honest default for a demotion the review never explained.
        reconciliation.Should().Contain("| Medium | [MEDIUM] duplicate rows | — |");
    }

    // ── Round 3: vocabularies derived from the stored corpus, not reasoned out ────────────────────────
    //
    // The previous disposition vocabulary was five phrase shapes argued from first principles. Run over
    // 810 stored review texts it matched 0 of 265 rows — a permanently blank column, which reads as a
    // working feature with nothing to report. Every rule below was instead read out of that corpus and
    // then measured back over it. The tests fix the shapes that measurement produced.

    [Fact]
    public void A_tally_that_also_describes_itself_is_still_a_tally()
    {
        // The end-of-line anchor was the bug: `2 HIGH/BLOCKER findings` was excluded, and the same line
        // continuing into its own description was not — so the commonest form of the tally got through.
        ReviewFindingReconciler
            .ParseFindings("- **2 HIGH/BLOCKER findings**: the allocation path has no executable test.\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_severity_roll_up_line_is_a_count_not_a_finding()
    {
        // Three counts on one line, and no other rule can see it: there is no leading digit for the tally
        // rule, and the counts are non-zero so the none-of-severity rule does not fire either.
        ReviewFindingReconciler
            .ParseFindings("## Findings: 0 Critical, 2 High, 1 Medium\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_line_saying_there_are_none_of_a_severity_is_not_a_finding_of_it()
    {
        // The most perverse row the old parse produced: a statement that nothing was found, recorded as a
        // finding that was then dropped.
        ReviewFindingReconciler
            .ParseFindings("## No high findings in the changed files\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_sentence_narrating_the_graders_decision_is_not_a_finding()
    {
        // This shape is where the corpus actually states dispositions. It has to be read as a reason and
        // never as a finding of its own, or the artifact reports the grading pass as a discarded finding.
        ReviewFindingReconciler
            .ParseFindings("- The review-grader confirmed the convention-path issue as HIGH.\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_bare_severity_label_with_no_text_is_not_a_finding()
    {
        // A label with nothing attached to it. Both forms appear in the corpus as section scaffolding.
        ReviewFindingReconciler
            .ParseFindings("- **MEDIUM**\n- **[QUESTION]**\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void The_word_informational_in_prose_does_not_create_a_finding()
    {
        // Removed on measurement, and the SWEEP is the point rather than this one word: every token in the
        // severity vocabulary was counted the same way over 4,469 real lead lines. `informational` appeared
        // 3 times and was label-shaped in 0 of them — pure prose, exactly as the bare word `question` was.
        // Every surviving token carries real labels (`blocker` 250/270, `high` 318/373, `medium` 264/304).
        ReviewFindingReconciler
            .ParseFindings("- Two additional informational compatibility notes were identified.\n")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_bold_Question_prefix_is_recognised_as_a_question()
    {
        // A second question convention alongside the bracketed tag, in 2 of 810 corpus texts. Until it was
        // recognised those items produced no row at all and nothing was logged — the silent kind of miss,
        // which is precisely the failure family this artifact exists to end.
        var findings = ReviewFindingReconciler.ParseFindings(
            "#### **Question:** does the retry budget reset per attempt?\nsrc/Foo.cs:10\n");

        findings.Should().ContainSingle();
        findings[0].IsQuestion.Should().BeTrue();
        findings[0].SeverityPhrase.Should().Be("Question");
    }

    [Fact]
    public void A_dependency_downgrade_in_the_reviewed_code_is_not_a_disposition()
    {
        // The corpus's dominant use of the word is about the PR's package versions, not about a finding —
        // the same trap the earlier bare `deduplicat` stem fell into. Two guards keep it out: the line
        // names no finding, and its `from … to …` lands on version numbers rather than severities.
        ReviewFindingReconciler
            .IsDispositionStatement("The dependency was downgraded from 1.25.1 to 1.24.1.")
            .Should().BeFalse();
    }

    [Fact]
    public void A_disposition_the_review_stated_about_a_finding_is_recognised()
    {
        // The corpus phrasing, which the invented vocabulary missed entirely: reviewers write "already
        // covered by", "subsumed by", "not raised as a separate finding" — not "superseded by".
        ReviewFindingReconciler
            .IsDispositionStatement("The rollout concern is already covered by the existing unresolved thread.")
            .Should().BeTrue();
    }

    [Fact]
    public void A_severity_move_needs_no_finding_noun_to_count_as_a_disposition()
    {
        // A severity-to-severity move cannot be about anything but a finding, so it qualifies alone. This
        // is the clause that keeps the honest "escalated to HIGH" sentences from needing a noun.
        ReviewFindingReconciler
            .IsDispositionStatement("Escalated to HIGH after checking the call sites.")
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_disposition_the_daemon_cannot_attribute_is_listed_not_attached()
    {
        // Measured over 260 shipped reviews: 28 of them state a disposition, but block-scoped quoting can
        // attach one to about 1 row in 250, because reviewers write it in a review-level grading section
        // rather than inside the finding. So the statement is quoted UNATTACHED. Welding it onto the
        // nearest row would be a guess, and a guessed attribution reads exactly like a recorded one.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", $"#### [BLOCKER] High — DI coupling\n{DiCouplingSite}\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody:
                $"#### [LOW] wiring detail\n{DiCouplingSite}\n"
                + "\n"
                + "## Grading\n"
                + "Two performance concerns were consolidated into the wiring finding.\n");

        var reconciliation = Reconciliation(files).Content;
        reconciliation.Should().Contain("## Disposition statements not tied to a row");
        reconciliation.Should().Contain("Two performance concerns were consolidated into the wiring finding.");
        // The row's own reason cell stays blank — the review said nothing inside that block.
        reconciliation.Should().Contain("| Low | [LOW] wiring detail | — |");
    }

    [Fact]
    public async Task A_review_that_stated_no_disposition_says_so_rather_than_omitting_the_section()
    {
        // An absent section is indistinguishable from a section that failed to build. The blank case has
        // to be stated, for the same reason the whole artifact exists.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", "#### [BLOCKER] High — unchecked cast\nsrc/Foo.cs:10 casts without a test.\n"),
        ]));

        var files = await BuildAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "#### [BLOCKER] High — unchecked cast\nsrc/Foo.cs:10 must be guarded.\n");

        Reconciliation(files).Content.Should()
            .Contain("The shipped review stated no disposition anywhere outside the findings above.");
    }

    [Fact]
    public async Task The_disposition_tally_is_logged_on_a_build_that_reconciled_nothing()
    {
        // The denominator is the point. A tally that only appears once there is something to report cannot
        // answer "is this rising?" — "0 severity-changed out of 0 reconciled" and "0 out of 40" are not the
        // same review, and a line that goes quiet on the empty case is indistinguishable from one that broke.
        // Zero roster nodes is the emptiest possible build, so it is the one that pins the claim.
        var logs = new CapturingLogger<object>();
        var builder = new ReviewNotesArtifactBuilder(
            new FakeTranscripts(descendant: [], root: [Entry("TextMessage", "reviewed alone")]),
            logs);

        await BuildAsync(builder, NewContext(), shippedReviewBody: "## Review\nNothing worth reporting.\n");

        logs.MessagesAtLevel(LogLevel.Information).Should().ContainSingle(
            m => m.Contains("reconciled 0 specialist finding(s)", StringComparison.Ordinal),
            "the tally is logged on every build, including the one where there was nothing to tally");
    }

    // ---------------------------------------------------------------------------------------------------
    // The structured findings record. The reconciliation markdown above answers "what happened on this PR";
    // these answer "what happened across every PR", which is a different question and cannot be asked of
    // prose. Every assertion here is about the record being COMPLETE and TYPED — a record that quietly holds
    // fewer findings than the reviewers produced would make a quiet review and a busy one look identical,
    // which is the exact failure this artifact exists to make visible.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Three findings from an architecture reviewer and the same three from a tests reviewer (the fake host
    /// hands every agent the same transcript), reconciled against a shipped review that carries all three.
    /// </summary>
    private const string ThreeFindings =
        "#### [BLOCKER] High — DI coupling\n"
        + $"{DiCouplingSite} resolves the module from the container directly.\n"
        + "\n"
        + "#### [MEDIUM] — unchecked cast\n"
        + "src/Foo.cs:10 casts without a test.\n"
        + "\n"
        + "#### [LOW] — naming\n"
        + "src/Bar.cs:77 shadows the field name.\n";

    private const string ThreeShipped =
        "#### [BLOCKER] High — DI coupling\n"
        + $"{DiCouplingSite} must be injected.\n"
        + "\n"
        + "#### [MEDIUM] — unchecked cast\n"
        + "src/Foo.cs:10 must be guarded.\n"
        + "\n"
        + "#### [LOW] — naming\n"
        + "src/Bar.cs:77 should be renamed.\n";

    [Fact]
    public async Task Every_finding_the_extractor_saw_reaches_the_record()
    {
        // The round trip, and the only assertion that can fail closed on a silent loss: the parsed count is
        // taken on a SEPARATE pass over the same text, so a row dropped between extraction and the record
        // makes the two disagree. (It cannot catch a finding the extractor never saw — that is a different
        // guarantee, and CountParsed's doc comment says so rather than letting this test imply it.)
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture"), Node("agent-2", "tests")),
            shippedReviewBody: ThreeShipped);

        var findings = built.Findings;
        findings.Compared.Should().BeTrue();
        findings.ParsedCount.Should().Be(6, "three findings from each of two reviewers");
        findings.RecordedCount.Should().Be(6);
        findings.Shortfall.Should().Be(0);
        findings.Findings.Should().HaveCount(6);

        // And per reviewer, so a shortfall names who it happened to rather than only that it happened.
        findings.Sources.Should().HaveCount(2);
        findings.Sources.Should().OnlyContain(s => s.Parsed == 3 && s.Recorded == 3);
        findings.Sources.Select(s => s.Label).Should().BeEquivalentTo(["architecture", "tests"]);
    }

    [Fact]
    public async Task Two_reviewers_sharing_a_label_are_each_credited_only_with_their_own_rows()
    {
        // A reviewer's LABEL is not its identity. It is `node.Name ?? node.Template`, and Name arrives off
        // the wire chosen by the model, so two specialists running the same template with no name of their
        // own are labelled identically — an ordinary roster, not a pathological one.
        //
        // Attributing rows by that label makes the per-source arithmetic impossible: a join on it fans out,
        // and every colliding source is credited with the WHOLE group's rows. The failure is silent, because
        // the global Shortfall is computed from totals that are still correct and so never trips the warning
        // at the call site — and the rows are permanent, since the transcripts they came from are not kept.
        //
        // The two invariants below are what "Recorded" has to mean for a shortfall to be a shortfall rather
        // than a restatement: the per-source counts must PARTITION the record, and no source may be credited
        // with more rows than it was parsed for.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture"), Node("agent-2", "architecture")),
            shippedReviewBody: ThreeShipped);

        var findings = built.Findings;
        findings.RecordedCount.Should().Be(6, "two reviewers contributed three findings each");
        findings.Sources.Should().HaveCount(2, "two roster nodes reviewed, whatever they are called");

        findings.Sources.Sum(s => s.Recorded).Should().Be(
            findings.RecordedCount,
            "the per-source counts must partition the record — a row belongs to exactly one reviewer, so "
                + "double-counting one reviewer's rows against another inflates the sum past the total");
        findings.Sources.Should().OnlyContain(
            s => s.Recorded <= s.Parsed,
            "no reviewer can contribute more rows than it had findings parsed out of it");
        findings.Sources.Should().OnlyContain(
            s => s.Parsed == 3 && s.Recorded == 3,
            "each of the two reviewers is credited with its own three findings and none of the other's");
    }

    [Fact]
    public async Task A_finding_is_recorded_with_its_severity_as_tokens_not_only_as_prose()
    {
        // Positive control, on a real citation from a live review (DiCouplingSite is the round-01
        // architecture finding from run 226). Severity has to survive as something a GROUP BY can use;
        // "[BLOCKER] High" as a string is a phrase, and bucketing on phrases is how a severity distribution
        // becomes a distribution of typos.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: ThreeShipped);

        var blocker = built.Findings.Findings.Single(f => f.Location == DiCouplingSite);
        blocker.Source.Should().Be("architecture", "the row is attributed to the reviewer that raised it");
        blocker.Template.Should().Be("reviewer", "and to the roster template it ran, so it traces back");
        blocker.Title.Should().Contain("DI coupling");
        blocker.Severity.Should().Contain("Blocker");
        blocker.SeverityTokens.Should().Contain("Blocker");
        blocker.Outcome.Should().Be("kept");
        blocker.ShippedTitle.Should().Contain("DI coupling");
    }

    [Fact]
    public async Task Findings_bucket_by_severity_without_reparsing_the_prose()
    {
        // The whole point: three severities in, three buckets out, from the stored record alone.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: ThreeShipped);

        var buckets = built.Findings.Findings
            .SelectMany(f => f.SeverityTokens)
            .GroupBy(t => t, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        buckets.Should().ContainKey("Blocker").WhoseValue.Should().Be(1);
        buckets.Should().ContainKey("Medium").WhoseValue.Should().Be(1);
        buckets.Should().ContainKey("Low").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task A_severity_change_is_recorded_with_both_sides_of_the_move()
    {
        // A demotion is the single most interesting event in a review and the one prose loses first. Both
        // severities must be on the row, or the record can say a change happened but not what it was.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry("TextMessage", $"#### [BLOCKER] High — DI coupling\n{DiCouplingSite} resolves directly.\n"),
        ]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: $"#### [MEDIUM] — DI coupling\n{DiCouplingSite} is a wiring detail.\n");

        var row = built.Findings.Findings.Should().ContainSingle().Subject;
        row.Outcome.Should().Be("severity-changed");
        row.SeverityTokens.Should().Contain("Blocker");
        row.ShippedSeverity.Should().Contain("Medium");
    }

    [Fact]
    public async Task The_record_spells_an_outcome_the_same_way_the_reconciliation_table_does()
    {
        // Two serialisations of one list. If they ever disagree about what an outcome is CALLED, a query
        // written against the artifact and a human reading the table are measuring different things while
        // both believing they agree.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: ThreeShipped);

        var table = Reconciliation(built.Files).Content;
        foreach (var row in built.Findings.Findings)
        {
            table.Should().Contain($"`{row.Outcome}`");
        }
    }

    [Fact]
    public async Task A_round_with_no_shipped_review_records_the_absence_rather_than_a_page_of_losses()
    {
        // "Not compared" and "not carried" are different facts. The record must be able to say the first
        // one, because a reader who cannot tell them apart reads an unreconciled round as a total loss —
        // and, worse, a query that counts rows would report the round as having produced no findings when
        // the reviewers in fact produced three.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(builder, NewContext(Node("agent-1", "architecture")));

        var findings = built.Findings;
        findings.Compared.Should().BeFalse();
        findings.ParsedCount.Should().Be(3, "the reviewer's findings exist whether or not anything shipped");
        findings.RecordedCount.Should().Be(0);
        findings.Shortfall.Should().Be(3);
        findings.Sources.Should().ContainSingle().Which.Parsed.Should().Be(3);
    }

    [Fact]
    public async Task A_round_whose_reviewers_found_nothing_records_a_zero_rather_than_no_row()
    {
        // The denominator case. An absent record and a record of zero findings are indistinguishable to
        // anyone querying later, and only one of them means the review was quiet.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", "No issues found in this area.")]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: "No blocking issues.\n");

        built.Findings.Round.Should().Be(1);
        built.Findings.Compared.Should().BeTrue();
        built.Findings.RecordedCount.Should().Be(0);
        built.Findings.Shortfall.Should().Be(0);
    }

    [Fact]
    public async Task Ordinary_review_structure_does_not_inflate_the_finding_count()
    {
        // The count is the headline number, so an extractor that over-matches is the failure that makes this
        // artifact actively misleading: it would report a rich review where there was a terse one, and no
        // assertion on severity or outcome would notice. Reviewers surround their findings with section
        // headings and plain bullets, and NEITHER may open a block.
        //
        // This test exists because a superset mutation of StartsFinding killed nothing in this class:
        // StartsFinding is only consulted on heading and top-level list-item lines, and every other fixture
        // here surrounds its findings with prose body lines, which cannot start a block however wide the
        // predicate gets. Only a fixture whose non-findings are themselves headings and bullets can see it.
        var builder = NewBuilder(new FakeTranscripts(
        [
            Entry(
                "TextMessage",
                "## Summary\n"
                + "- reviewed the DI wiring and the cast sites\n"
                + "- ran the tests locally\n"
                + "\n"
                + ThreeFindings
                + "\n"
                + "### Notes\n"
                + "- no further concerns\n"),
        ]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture")),
            shippedReviewBody: ThreeShipped);

        built.Findings.ParsedCount.Should().Be(3, "five of the eight structural lines carry no severity");
        built.Findings.RecordedCount.Should().Be(3);
        built.Findings.Findings.Select(f => f.Title).Should().NotContain(t => t.Contains("Summary"));
    }

    [Fact]
    public async Task The_record_and_the_rendered_table_come_off_one_reconcile_of_one_list()
    {
        // The load-bearing property, asserted rather than commented. The markdown and the artifact are two
        // serialisations of a single `reconciled` local, produced on a single call. Two reconcile passes
        // would eventually disagree, and the disagreement would be SILENT — a query and a human reading the
        // same PR would both be confident and only one of them right. A refactor that splits them fails
        // here instead of drifting.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        var built = await BuildFullAsync(
            builder,
            NewContext(Node("agent-1", "architecture"), Node("agent-2", "tests")),
            shippedReviewBody: ThreeShipped);

        var table = Reconciliation(built.Files).Content;

        // Same population: one numbered data row per record, no more and no fewer.
        var dataRows = table
            .Split('\n')
            .Count(l => l.StartsWith("| ", StringComparison.Ordinal)
                && char.IsDigit(l.AsSpan(2)[0]));
        dataRows.Should().Be(built.Findings.RecordedCount);

        // Same content: every record's location, reviewer and outcome spelling is on the table it was
        // rendered beside. A second reconcile over different inputs breaks at least one of the three.
        foreach (var row in built.Findings.Findings)
        {
            table.Should().Contain(row.Location);
            table.Should().Contain(row.Source);
            table.Should().Contain($"`{row.Outcome}`");
        }
    }

    [Fact]
    public async Task The_record_carries_the_provenance_a_later_query_needs_to_be_windowed()
    {
        // This artifact kind did not exist before it started being written, so its absence on older runs
        // says nothing about those reviews. Without a first-write timestamp ON THE ROW, a query six months
        // from now reads the pre-write period as "zero findings" — the one conclusion the data cannot
        // support. The prompt hash is the other half: a finding-count change across a prompt change is a
        // different event from one within a single prompt.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", ThreeFindings)]));

        // A non-null hash on the run, so this assertion can fail. With the fixture's default null it would
        // pass against a Build() that hardcoded null and never read the run at all.
        var built = await builder.BuildAsync(
            NewRun("tpl-sha256-abc123"), NewRepo(), "PRs/lmdotnettools-250",
            NewContext(Node("agent-1", "architecture")), CancellationToken.None, ThreeShipped);

        var captured = DateTimeOffset.Parse(
            built.Findings.CapturedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        captured.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        built.Findings.PromptTemplateHash.Should().Be("tpl-sha256-abc123");

        // And which text these rows correspond to, recorded rather than left to be inferred later — the
        // answer stops being obvious the day the infra-narration filter splits the posted comment from the
        // stored prose.
        built.Findings.DerivedFrom.Should().Be("reviewer-transcripts-via-reconciler");
    }
}
