using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class ReviewNotesArtifactBuilderTests
{
    private const string RootThread = "thread-root";

    private sealed class FakeTranscripts : IReviewAgentTranscriptSource
    {
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry> _descendant;
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry>? _root;
        private readonly Exception? _rootFailure;

        public FakeTranscripts(
            IReadOnlyList<ReviewAgentTranscriptEntry> descendant,
            IReadOnlyList<ReviewAgentTranscriptEntry>? root = null,
            Exception? rootFailure = null
        )
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
            CancellationToken ct
        )
        {
            RequestedAgentIds.Add(agentId);
            return Task.FromResult(_descendant);
        }

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
            string rootThreadId,
            CancellationToken ct
        )
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

    private static ReviewSubAgentNode NodeOnModel(
        string agentId,
        string name,
        string? model,
        int? tier = null,
        string? source = null,
        string? requestedEffort = null,
        string? shapedEffort = null
    ) =>
        Node(agentId, name) with
        {
            EffectiveModelId = model,
            EffectiveModelIntelligence = tier,
            ModelSelectionSource = source,
            RequestedReasoningEffort = requestedEffort,
            ShapedReasoningEffort = shapedEffort,
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
            Roster: new ReviewSubAgentTreeSnapshot(nodes)
        );

    private static ReviewNotesArtifactBuilder NewBuilder(IReviewAgentTranscriptSource? transcripts) =>
        new(transcripts, NullLogger.Instance);

    private static Task<ReviewNotesArtifacts> BuildFullAsync(
        ReviewNotesArtifactBuilder builder,
        ReviewNotesArtifactContext context
    ) => builder.BuildAsync(NewRun(), NewRepo(), "PRs/lmdotnettools-250", context, CancellationToken.None);

    private static async Task<IReadOnlyList<ReviewArtifactFile>> BuildAsync(
        ReviewNotesArtifactBuilder builder,
        ReviewNotesArtifactContext context
    ) => (await BuildFullAsync(builder, context)).Files;

    [Fact]
    public async Task Tool_traffic_accounting_and_reasoning_are_dropped_and_the_omission_is_disclosed()
    {
        // One conclusion buried in the keystroke log of how it was reached. Only the conclusion is worth
        // carrying into the next round's context window.
        var transcripts = new FakeTranscripts([
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
        var transcripts = new FakeTranscripts([Entry("TextMessage", "   "), Entry("TextMessage", "real content")]);
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
            root: [Entry("TextMessage", "VERDICT: request changes — see finding 1.")]
        );
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var lead = files.Single(f =>
            f.RelativePath.EndsWith("PR_Findings_01_00_lead-reviewer.md", StringComparison.Ordinal)
        );
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
            rootFailure: new InvalidOperationException("host returned 404")
        );
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var lead = files.Single(f =>
            f.RelativePath.EndsWith("PR_Findings_01_00_lead-reviewer.md", StringComparison.Ordinal)
        );
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
            root: [Entry("TextMessage", "reviewed alone; no specialists needed")]
        );
        var builder = NewBuilder(transcripts);

        var files = await BuildAsync(builder, NewContext());

        files
            .Select(f => f.RelativePath)
            .Should()
            .BeEquivalentTo([
                "PRs/lmdotnettools-250/PR_Context_01.md",
                "PRs/lmdotnettools-250/PR_Findings_01_00_lead-reviewer.md",
            ]);
        files
            .Single(f => f.RelativePath.Contains("lead-reviewer", StringComparison.Ordinal))
            .Content.Should()
            .Contain("no specialists needed");
    }

    [Fact]
    public async Task Transcript_volume_is_bounded_so_one_verbose_reviewer_cannot_eat_the_next_rounds_context()
    {
        // Retained prose, not tool traffic — the budget has to hold even when every message is legitimate.
        var entries = Enumerable
            .Range(0, 200)
            .Select(i => Entry("TextMessage", $"finding {i}: " + new string('x', 4_000)))
            .ToArray();
        var builder = NewBuilder(new FakeTranscripts(entries));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings
            .Content.Length.Should()
            .BeLessThan(
                30_000,
                "the whole notes directory is concatenated into the next round's prompt and the extractor's"
            );
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
            new FakeTranscripts([
                Entry("ToolCallMessage", "{\"name\":\"Grep\",\"pattern\":\"await\"}", role: "user"),
                Entry("ToolsCallResultMessage", "…900 matches…", role: "user"),
                // Survives the tool-traffic filter, and is still not one word this agent wrote.
                Entry("TextMessage", "REVIEW BRIEF: examine the telemetry module.", role: "user"),
            ]),
            logs
        );

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
        logs.MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle()
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

    [Fact]
    public void A_tally_that_also_describes_itself_is_still_a_tally()
    {
        // The end-of-line anchor was the bug: `2 HIGH/BLOCKER findings` was excluded, and the same line
        // continuing into its own description was not — so the commonest form of the tally got through.
        ReviewFindingReconciler
            .ParseFindings("- **2 HIGH/BLOCKER findings**: the allocation path has no executable test.\n")
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_severity_roll_up_line_is_a_count_not_a_finding()
    {
        // Three counts on one line, and no other rule can see it: there is no leading digit for the tally
        // rule, and the counts are non-zero so the none-of-severity rule does not fire either.
        ReviewFindingReconciler.ParseFindings("## Findings: 0 Critical, 2 High, 1 Medium\n").Should().BeEmpty();
    }

    [Fact]
    public void A_line_saying_there_are_none_of_a_severity_is_not_a_finding_of_it()
    {
        // The most perverse row the old parse produced: a statement that nothing was found, recorded as a
        // finding that was then dropped.
        ReviewFindingReconciler.ParseFindings("## No high findings in the changed files\n").Should().BeEmpty();
    }

    [Fact]
    public void A_sentence_narrating_the_graders_decision_is_not_a_finding()
    {
        // This shape is where the corpus actually states dispositions. It has to be read as a reason and
        // never as a finding of its own, or the artifact reports the grading pass as a discarded finding.
        ReviewFindingReconciler
            .ParseFindings("- The review-grader confirmed the convention-path issue as HIGH.\n")
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_bare_severity_label_with_no_text_is_not_a_finding()
    {
        // A label with nothing attached to it. Both forms appear in the corpus as section scaffolding.
        ReviewFindingReconciler.ParseFindings("- **MEDIUM**\n- **[QUESTION]**\n").Should().BeEmpty();
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
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void A_bold_Question_prefix_is_recognised_as_a_question()
    {
        // A second question convention alongside the bracketed tag, in 2 of 810 corpus texts. Until it was
        // recognised those items produced no row at all and nothing was logged — the silent kind of miss,
        // which is precisely the failure family this artifact exists to end.
        var findings = ReviewFindingReconciler.ParseFindings(
            "#### **Question:** does the retry budget reset per attempt?\nsrc/Foo.cs:10\n"
        );

        findings.Should().ContainSingle();
        findings[0].IsQuestion.Should().BeTrue();
        findings[0].SeverityPhrase.Should().Be("Question");
    }

    // ── Which model ran which sub-agent (#552) ────────────────────────────────────────────────────
    // The value was already computed by the host and already carried by two DTOs; it was dropped in the
    // middle of the chain, so the context file showed one Model row for the whole run and the per-agent
    // rows showed only Template / Status / Depth / Agent id — leaving "did the fan-out run on the model
    // the run did?" unanswerable from the artifacts.

    [Fact]
    public async Task A_sub_agent_whose_model_the_host_reported_has_it_in_both_tables()
    {
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", "finding")]));

        var files = await BuildAsync(
            builder,
            NewContext(
                NodeOnModel(
                    "agent-1",
                    "architecture",
                    "gpt-5.6-sol",
                    tier: 5,
                    source: "spawn-tier",
                    requestedEffort: "xhigh",
                    shapedEffort: "xhigh"
                )
            )
        );

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Should().Contain("| Model | gpt-5.6-sol |");
        findings.Content.Should().Contain("| Model tier | 5 |");
        findings
            .Content.Should()
            .Contain(
                "| Model source | spawn-tier |",
                "the model id alone cannot tell a tier that resolved to it from a caller that named it outright"
            );
        findings.Content.Should().Contain("| Requested effort | xhigh |");
        findings.Content.Should().Contain("| Shaped effort | xhigh |");

        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("| # | Agent | Model | Template | Status | Findings file |");
        contextFile.Content.Should().Contain("gpt-5.6-sol");
    }

    [Fact]
    public async Task Provider_telemetry_cannot_add_markdown_table_columns()
    {
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", "finding")]));
        var node = NodeOnModel(
            "agent-1",
            "architecture | forged-agent-cell",
            "gpt-5.6-sol | forged-model-cell",
            source: "type-policy | forged-source-cell",
            requestedEffort: "xhigh | forged-request-cell",
            shapedEffort: "xhigh | forged-shaped-cell"
        ) with
        {
            Template = "reviewer | forged-template-cell",
            FailureCode = "timeout | forged-failure-cell",
        };

        var files = await BuildAsync(builder, NewContext(node));

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Should().Contain("| Model | gpt-5.6-sol \\| forged-model-cell |");
        findings.Content.Should().Contain("| Model source | type-policy \\| forged-source-cell |");
        findings.Content.Should().Contain("| Requested effort | xhigh \\| forged-reque… |");
        findings.Content.Should().Contain("| Shaped effort | xhigh \\| forged-shape… |");
        findings.Content.Should().Contain("| Template | reviewer \\| forged-template-cell |");
        findings.Content.Should().Contain("| Failure code | timeout \\| forged-failure-cell |");

        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("architecture \\| forged-agent-cell");
        contextFile.Content.Should().Contain("reviewer \\| forged-template-cell");
    }

    [Fact]
    public async Task A_sub_agent_with_no_recorded_model_says_unrecorded_and_never_borrows_the_runs_model()
    {
        // The whole point of the column. A host that predates the field omits it, and a fallback to the
        // run-level model would render a guess in the same cell as a measurement — which would answer
        // "did the fan-out run on the run's model?" with "yes" by construction, for every run, forever.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", "finding")]));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Should().Contain("| Model | (unrecorded) |");
        findings
            .Content.Should()
            .NotContain("| Model | test-model |", "test-model is the RUN's model and this agent's is not recorded");
        findings.Content.Should().Contain("| Model tier | (unrecorded) |");
        findings.Content.Should().Contain("| Model source | (unrecorded) |");
        findings.Content.Should().Contain("| Requested effort | (unrecorded) |");
        findings.Content.Should().Contain("| Shaped effort | (unrecorded) |");

        var contextFile = files.Single(f => f.RelativePath.EndsWith("PR_Context_01.md", StringComparison.Ordinal));
        contextFile.Content.Should().Contain("| 1 | architecture | (unrecorded) | reviewer |");
    }

    [Fact]
    public async Task A_recorded_model_and_the_runs_model_being_equal_is_still_reported_as_recorded()
    {
        // The measurement the owner is actually after: sub-agents matching the run's model is a FINDING, and
        // it has to be distinguishable from the artifact having nothing to say. Same rendered string in both
        // cases would make the answer unreadable in exactly the case it matters.
        var builder = NewBuilder(new FakeTranscripts([Entry("TextMessage", "finding")]));

        var files = await BuildAsync(
            builder,
            NewContext(NodeOnModel("agent-1", "architecture", "test-model", source: "parent"))
        );

        var findings = files.Single(f => f.RelativePath.Contains("_01_architecture", StringComparison.Ordinal));
        findings.Content.Should().Contain("| Model | test-model |");
        findings.Content.Should().NotContain("| Model | (unrecorded) |");
        findings
            .Content.Should()
            .Contain(
                "| Model source | parent |",
                "'parent' against every node is what says the fan-out inherited and no per-agent routing ran"
            );
    }
}
