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

    private static ReviewRun NewRun() =>
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

    private static Task<IReadOnlyList<ReviewArtifactFile>> BuildAsync(
        ReviewNotesArtifactBuilder builder,
        ReviewNotesArtifactContext context) =>
        builder.BuildAsync(NewRun(), NewRepo(), "PRs/lmdotnettools-250", context, CancellationToken.None);

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
            "PRs/lmdotnettools-250/PR_Findings_01_00_lead-reviewer.md",
        ]);
        files.Single(f => f.RelativePath.Contains("lead-reviewer", StringComparison.Ordinal))
            .Content.Should().Contain("no specialists needed");
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

    // ── The lead's own conclusions must outrank everything it was handed ──────────────────────────────
    //
    // Live shape of a lead-reviewer transcript, in the order the host persists it:
    //
    //     [User TextMessage]  the review brief the daemon itself composed  (~14 KB before truncation)
    //     [User NotifyMessage] × N   one per delegate that finished
    //     [Assistant TextMessage]    the provisional answer
    //     [User TextMessage]         the synthesis prompt
    //     [Assistant TextMessage]    THE REVIEW
    //
    // The agent's own answers are structurally LAST, so a single chronological walk that stops at the first
    // over-budget entry cut exactly them. Measured over 42 live artifacts before this changed: 6 held no
    // assistant message at all, 5 more held one of two — 11/42 = 26%.

    // ── Sizing these entries is the whole test, and the obvious sizing proves nothing ────────────────
    //
    // The first version of this test used context entries WELL OVER MaxEntryChars, on the assumption that
    // huge context is what crowds out the agent's turns. It isn't, and that version passed even against a
    // build with the tiering removed. Oversized context is truncated to the cap and then, if the truncated
    // block still doesn't fit the remaining budget, SKIPPED — and skip-and-continue moves on to the next
    // entry, so the small assistant turns behind it sail into the leftover space no matter which order the
    // walk uses. Tiered and chronological give the same answer, and the test says nothing.
    //
    // What separates the two algorithms is context that FITS AND FILLS: entries under the per-entry cap
    // that together consume nearly the whole artifact budget, leaving a remainder too small for the turns
    // that come after them. Only then does a chronological walk actually starve the agent's own answers.
    //
    // Hence 5,800-char context bodies (under the 6,000 cap, so nothing is truncated or skipped) and
    // 600-char turns. Two context entries take ~11,700 of the 12,000 budget; ~300 is left, and a turn needs
    // ~650. The margins hold for any per-entry rendering overhead between 0 and 100 chars, so this does not
    // silently decay if the header format changes.

    /// <summary>Context that fits under the per-entry cap and nearly fills the artifact budget — the only
    /// shape that can starve the turns behind it. The daemon's real brief alone took 51% of the budget.</summary>
    private static ReviewAgentTranscriptEntry Brief() =>
        Entry("TextMessage", "REVIEW BRIEF: " + new string('b', 5_800), role: "user");

    private static ReviewAgentTranscriptEntry Notice(string delegateName, string finding) =>
        Entry(
            "NotifyMessage",
            $"<notification kind=\"subagent-completion\" label=\"{delegateName}\">{finding}"
                + new string('n', 5_800) + "</notification>",
            role: "user");

    /// <summary>An assistant turn large enough that it cannot slip into the crumbs a filled budget leaves.</summary>
    private static ReviewAgentTranscriptEntry Turn(string headline) =>
        Entry("TextMessage", headline + " " + new string('t', 600));

    [Fact]
    public async Task The_leads_own_conclusions_survive_a_brief_and_notices_that_would_have_filled_the_budget()
    {
        var builder = NewBuilder(new FakeTranscripts(
            descendant: [Entry("TextMessage", "specialist says")],
            root:
            [
                Brief(),
                Notice("code-reviewer:schema-compatibility-review", "SCHEMA NOTICE. "),
                Turn("PROVISIONAL: two blockers so far, children still running."),
                Entry("TextMessage", "SYNTHESIS PROMPT", role: "user"),
                Turn("VERDICT: request changes — BLOCKER 1 schema, BLOCKER 2 rollout."),
            ]));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));
        var lead = files.Single(f => f.RelativePath.EndsWith("_00_lead-reviewer.md", StringComparison.Ordinal));

        // The whole point. Both of the lead's own turns are present, in full.
        lead.Content.Should().Contain("PROVISIONAL: two blockers so far");
        lead.Content.Should().Contain("VERDICT: request changes — BLOCKER 1 schema, BLOCKER 2 rollout.");

        // What gave way instead: context the daemon can reproduce. It is named, counted, and the reader is
        // told where the full copy lives, so this can never read as a reviewer that had nothing to say.
        lead.Content.Should().Contain("further context message(s) omitted");
        lead.Content.Should().Contain("findings file carries its result in full");

        // Selection is tiered; presentation is not. A reader needs the conversation in the order it happened.
        lead.Content.IndexOf("PROVISIONAL:", StringComparison.Ordinal)
            .Should().BeLessThan(
                lead.Content.IndexOf("VERDICT:", StringComparison.Ordinal),
                "entries are still rendered chronologically — only the selection is tiered");
    }

    [Fact]
    public async Task Delegate_completion_notices_are_still_kept_when_the_budget_allows()
    {
        // The fix must NOT be "skip every User-role entry". Delegate completion notices carry that role, and
        // on a small review they are the only in-thread record that a delegate reported at all. Verified
        // against the live store before deprioritising them: across 43 PRs the per-delegate PR_Findings_*
        // files were always a superset of these notices (77 files vs 41 notices, zero PRs the other way),
        // and 90-96% of a notice's substantive lines appear verbatim in its own delegate's file. That makes
        // them safe to RANK BELOW the lead's own turns — not safe to discard.
        var builder = NewBuilder(new FakeTranscripts(
            descendant: [Entry("TextMessage", "specialist says")],
            root:
            [
                Entry("TextMessage", "brief", role: "user"),
                Entry("NotifyMessage", "DELEGATE REPORTED: missing null check", role: "user"),
                Entry("TextMessage", "VERDICT: approve with comments."),
            ]));

        var files = await BuildAsync(builder, NewContext(Node("agent-1", "architecture")));
        var lead = files.Single(f => f.RelativePath.EndsWith("_00_lead-reviewer.md", StringComparison.Ordinal));

        lead.Content.Should().Contain("DELEGATE REPORTED: missing null check");
        lead.Content.Should().Contain("brief");
        lead.Content.Should().Contain("VERDICT: approve with comments.");
        lead.Content.Should().NotContain("context message(s) omitted");
    }

    [Fact]
    public async Task Dropping_one_of_the_agents_own_turns_is_a_warning_not_just_a_line_in_a_file()
    {
        // The operator signal. Before this, the only trace of a dropped conclusion was a marker inside a file
        // nobody opens, while the daemon's summary line reported the artifact count as a success — 138 times.
        var logs = new CapturingLogger<object>();
        var builder = new ReviewNotesArtifactBuilder(
            new FakeTranscripts(
                descendant: [],
                root:
                [
                    Entry("TextMessage", "ANSWER ONE: " + new string('a', 5_000)),
                    Entry("TextMessage", "ANSWER TWO: " + new string('b', 5_000)),
                    Entry("TextMessage", "ANSWER THREE: " + new string('c', 5_000)),
                ]),
            logs);

        var files = await BuildAsync(builder, NewContext());
        var lead = files.Single(f => f.RelativePath.EndsWith("_00_lead-reviewer.md", StringComparison.Ordinal));

        // The file says which kind of loss this was — the agent's own words, not surrounding context.
        lead.Content.Should().Contain("of this agent's OWN");
        lead.Content.Should().Contain("only partly recorded");

        // And so does the log, at Warning, naming the agent.
        logs.MessagesAtLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("own turn(s) did not fit").And.Contain("lead reviewer (primary)");
    }

    [Fact]
    public async Task An_oversized_own_turn_is_truncated_in_place_never_dropped_whole()
    {
        // A findings file whose entire body is the prompt that produced it is worse than one that overran its
        // budget. What makes that impossible is the relationship between the two constants, pinned below: one
        // entry can never cost the whole artifact budget, so the first own turn always fits.
        UntrustedTranscriptText.MaxEntryChars.Should().BeLessThan(
            UntrustedTranscriptText.MaxArtifactChars,
            "the first own turn is admitted against the whole artifact budget, so a single entry must never "
                + "be able to exhaust it — otherwise a lead file could come out holding the prompt and no answer");

        var builder = NewBuilder(new FakeTranscripts(
            descendant: [],
            root: [Entry("TextMessage", "SOLE VERDICT: " + new string('v', 40_000))]));

        var files = await BuildAsync(builder, NewContext());
        var lead = files.Single(f => f.RelativePath.EndsWith("_00_lead-reviewer.md", StringComparison.Ordinal));

        lead.Content.Should().Contain("SOLE VERDICT:");
        // Bounded, and the reader is told by exactly how much — never a quiet clip.
        lead.Content.Should().Contain("daemon: truncated");
        lead.Content.Should().NotContain("of this agent's OWN");
    }

    [Fact]
    public void A_failed_read_that_the_tool_layer_called_a_success_is_still_counted()
    {
        // THE case this counter exists for. A sandbox Read of a missing path returns a SUCCESSFUL tool
        // result whose text says the file is not there; the agent is handed that text and moves on, and
        // nothing between the tool and the review body notices. 167 of these for one reference document
        // across 82 live review threads went unreported by every one of those reviews.
        var counted = ReviewNotesArtifactBuilder.CountFailedToolResults(
        [
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"File does not exist yet: /marketplaces/gb/x.md"}]}""",
                role: "user"),
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"{\"body\":\"\",\"status\":\"upstream_error\"}"}]}""",
                role: "user"),
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"/bin/sh: 1: dotnet: not found  [Exit code: 127]"}]}""",
                role: "user"),
        ]);

        counted.NotFound.Should().Be(1);
        counted.Denied.Should().Be(1);
        counted.Error.Should().Be(1);
        counted.Total.Should().Be(3);
    }

    [Fact]
    public void A_timeout_and_a_transcript_refusal_are_counted_in_their_own_buckets()
    {
        // Both families were found by running the classifier over 22,564 live tool results rather than by
        // reasoning about it, and both were being counted as ordinary content: 302 timeouts and 223 transcript
        // refusals. Together that is more failures than the not-found population this counter was built for.
        var counted = ReviewNotesArtifactBuilder.CountFailedToolResults(
        [
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"Error: Error: Command timed out after 30 seconds"}]}""",
                role: "user"),
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"You cannot read that agent's transcript."}]}""",
                role: "user"),
        ]);

        counted.Timeout.Should().Be(1);
        counted.Denied.Should().Be(1);
        counted.Total.Should().Be(2);
    }

    [Fact]
    public void Only_tool_traffic_is_counted_so_a_reviewer_discussing_a_failure_is_not_one()
    {
        // The counter's entire value is that it is INDEPENDENT of the reviewer's account. Counting an
        // assistant turn would fold the agent's own words back into the measurement, and a review that
        // correctly reported a missing file would then read as a review with more failures than one that
        // stayed silent — inverting the signal.
        var counted = ReviewNotesArtifactBuilder.CountFailedToolResults(
        [
            Entry("TextMessage", "I could not read the reference: File does not exist yet: /x.md"),
            Entry("UsageMessage", "File does not exist yet: /x.md", role: "user"),
            Entry("ToolsCallResultMessage", "   ", role: "user"),
        ]);

        counted.Total.Should().Be(0);
    }

    [Fact]
    public void A_tool_result_that_succeeded_is_not_counted_however_much_it_returned()
    {
        // The false-positive side, which matters more than the false-negative side here: this number is
        // read as "how much broke", so a count that grows with how much code the review OPENED would be
        // worse than no count at all. Both shapes below are ordinary successful reads.
        var counted = ReviewNotesArtifactBuilder.CountFailedToolResults(
        [
            // A source file that happens to discuss missing paths, well past the marker window.
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":" """ + new string('/', 400)
                    + """ // throws when the configured path does not exist"}]}""",
                role: "user"),
            // A command that ran and succeeded — the marker is present and its value is zero.
            Entry(
                "ToolsCallResultMessage",
                """{"tool_call_results":[{"result":"16 passed\n\n[Exit code: 0]"}]}""",
                role: "user"),
        ]);

        counted.Total.Should().Be(0);
    }
}
