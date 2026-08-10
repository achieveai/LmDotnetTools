using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using Microsoft.Extensions.Logging;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The lead reviewer's MID-REVIEW turns — the ones the daemon never drove — and whether they survive into the
/// notes artifact.
/// <para>
/// The daemon drives exactly two turns on a review thread: the provisional, and the synthesis. Everything the
/// lead agent says in between — replying to a sub-agent's completion notice — runs on the host's initiative.
/// <c>AgentTextCollector.CollectAsync</c> sees one <c>ExecuteRunAsync</c> per turn and
/// <c>S2SReviewAgent.ExecuteRunAsync</c> yields one message per poll, so those turns are invisible to the
/// daemon <b>by construction</b>: they never pass through the agent seam at all. The transcript read in
/// <see cref="ReviewNotesArtifactBuilder"/> is the only thing in the daemon that can see them.
/// </para>
/// <para>
/// On run 165 (PR 5505268) that was five host runs against two captured, and the three orphans carried a
/// <c>[BLOCKER][HIGH]</c> and a rationale for a finding the reviewer deliberately declined. The synthesis turn
/// happened to restate the blocker — model discretion, not a mechanism.
/// </para>
/// <para>
/// <b>Why this is a separate file from the budget tests.</b> Those pin how much is kept; these pin
/// <i>whose</i> turns are kept. The retention is real but incidental: it falls out of
/// <c>IsOwnTurn</c> being a ROLE comparison, so every assistant-role entry is tier 1 regardless of who drove
/// it. Nothing in a <see cref="ReviewAgentTranscriptEntry"/> could express the other reading even
/// deliberately — the record is
/// <c>(MessageType, Role, FromAgent, TimestampUtc, Body)</c>, carrying no run id and no input id. A future
/// change that tiers on anything finer would re-open the defect silently, and these tests are what would say
/// so.
/// </para>
/// </summary>
public sealed class UndrivenTurnRetentionTests
{
    private const string RootThread = "thread-fd92deccf6854800b6db80c2692ea9ae";

    /// <summary>Run 165's three orphaned mid-review turns, at the character counts the audit measured.</summary>
    private const string SparkBlocker = "[BLOCKER][HIGH] Spark payload rollout compatibility";
    private const string DeclinedRationale = "Declined: the ECS snapshot already covers this path";
    private const string ThirdOrphan = "Posted the compatibility finding to the PR";

    private sealed class FakeTranscripts : IReviewAgentTranscriptSource
    {
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry> _root;
        private readonly IReadOnlyList<ReviewAgentTranscriptEntry> _descendant;

        public FakeTranscripts(
            IReadOnlyList<ReviewAgentTranscriptEntry> root,
            IReadOnlyList<ReviewAgentTranscriptEntry>? descendant = null)
        {
            _root = root;
            _descendant = descendant ?? [];
        }

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
            string rootThreadId, string agentId, CancellationToken ct) => Task.FromResult(_descendant);

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
            string rootThreadId, CancellationToken ct) => Task.FromResult(_root);
    }

    /// <summary>
    /// A transcript entry whose body is exactly <paramref name="totalChars"/> long and still greppable. Length
    /// is the whole point here: these tests are about what fits, so a body that only approximates the measured
    /// size would prove something about a review that never happened.
    /// </summary>
    private static ReviewAgentTranscriptEntry Turn(string marker, int totalChars, string role = "assistant") =>
        new(
            "TextMessage",
            role,
            FromAgent: null,
            TimestampUtc: null,
            Body: marker + new string('x', Math.Max(0, totalChars - marker.Length)));

    private static ReviewRun NewRun() =>
        new()
        {
            RepoId = 1,
            PrId = "5505268",
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
            Provider = "azure-devops",
            OrgOrOwner = "o365exchange",
            Project = "Weve_DA",
            RepoName = "Nova",
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
            NotesDir: "/store/PRs/nova-5505268",
            PrevHeadSha: null,
            Roster: new ReviewSubAgentTreeSnapshot(nodes));

    private static async Task<string> LeadFileAsync(params ReviewAgentTranscriptEntry[] rootTranscript) =>
        (await LeadFileAndLogsAsync(rootTranscript)).Lead;

    private static async Task<(string Lead, CapturingLogger<ReviewNotesArtifactBuilder> Logs)>
        LeadFileAndLogsAsync(params ReviewAgentTranscriptEntry[] rootTranscript)
    {
        var logs = new CapturingLogger<ReviewNotesArtifactBuilder>();
        var builder = new ReviewNotesArtifactBuilder(new FakeTranscripts(rootTranscript), logs);
        var files = (await builder.BuildAsync(
            NewRun(), NewRepo(), "PRs/nova-5505268", NewContext(), CancellationToken.None)).Files;

        var lead = files
            .Single(f => f.RelativePath.Contains("lead-reviewer", StringComparison.Ordinal))
            .Content;
        return (lead, logs);
    }

    /// <summary>
    /// Run 165's exact shape. Five assistant turns; the daemon holds its own copy of two of them. The three it
    /// never saw must reach the file, because after the host's 24h retention sweep this artifact is the only
    /// place they exist.
    /// </summary>
    [Fact]
    public async Task The_three_turns_the_daemon_never_drove_reach_the_lead_artifact()
    {
        var lead = await LeadFileAsync(
            Turn("PROVISIONAL", 1_549),
            Turn(SparkBlocker, 600),
            Turn(DeclinedRationale, 421),
            Turn(ThirdOrphan, 601),
            Turn("SYNTHESIS", 5_491));

        lead.Should().Contain(
            SparkBlocker,
            "a blocker the reviewer raised mid-review is a finding whether or not the synthesis turn "
                + "happened to restate it — on run 165 it did, but nothing checks that and nothing would "
                + "notice if it stopped");
        lead.Should().Contain(
            DeclinedRationale,
            "why a finding was DECLINED is the reasoning a later round most needs and can least reconstruct");
        lead.Should().Contain(ThirdOrphan);
        lead.Should().NotContain(
            "of this agent's OWN",
            "all five turns fit the budget at these sizes, so no omission notice belongs in the file");
    }

    /// <summary>
    /// The ordering property, isolated. This is the one that would catch a regression: with the budget bound,
    /// the undriven turns must displace the daemon's own brief, not the other way round.
    /// <para>
    /// Selection used to be a single chronological walk, and transcript order puts the brief and the delegate
    /// notices AHEAD of the lead's replies — so the reviewer's own words were structurally last and
    /// structurally the first thing cut. A change that reverted to chronological admission would leave every
    /// content assertion in the test above still passing at run 165's comfortable sizes, and would silently
    /// resume dropping turns on exactly the large reviews where the most was said.
    /// </para>
    /// <para>
    /// <b>The sizes are load-bearing and were derived, not guessed.</b> A rendered block costs its body plus
    /// 46 characters for an assistant entry and 41 for a user one (heading, role, message type, the fence and
    /// its newlines). Two context entries at 6,000 and 5,800 therefore occupy 11,882 of the 12,000 budget and
    /// still FIT — which is the whole point. Context that merely overflows would be skipped, leaving room for
    /// the small assistant turns behind it, and the test would pass under chronological admission too. Only
    /// context that fits and crowds them out separates the two orderings; an earlier version of this test used
    /// oversized context and passed against both.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Undriven_turns_displace_the_daemons_own_brief_rather_than_the_reverse()
    {
        var lead = await LeadFileAsync(
            Turn("THE-DAEMONS-BRIEF", 6_000, role: "user"),
            Turn("DELEGATE-NOTICE", 5_800, role: "user"),
            Turn(SparkBlocker, 600),
            Turn(DeclinedRationale, 421),
            Turn(ThirdOrphan, 601));

        lead.Should().Contain(
            SparkBlocker,
            "chronologically this turn sits behind 11,882 characters of context that fit — it survives only "
                + "because own turns are selected first");
        lead.Should().Contain(DeclinedRationale);
        lead.Should().Contain(ThirdOrphan);
        lead.Should().NotContain(
            "of this agent's OWN",
            "the three undriven turns total 1,760 rendered characters against a 12,000 budget; if any was "
                + "dropped, context was admitted ahead of them and the tiering has inverted");
        lead.Should().Contain(
            "further context message(s) omitted",
            "the budget genuinely bound — without something being dropped the assertions above would pass "
                + "vacuously, proving only that everything fit");
    }

    /// <summary>
    /// The honest-failure case. When the assistant turns alone overrun the budget, some are dropped — that is
    /// the design — but the file must SAY so, and the operator log must carry it too. A capped transcript that
    /// reads as complete is how a reviewer's missing conclusion looks like a reviewer that had no conclusion.
    /// </summary>
    [Fact]
    public async Task An_overrun_of_undriven_turns_is_disclosed_rather_than_silently_truncated()
    {
        var (lead, logs) = await LeadFileAndLogsAsync(
            Turn("TURN-1", 5_000),
            Turn(SparkBlocker, 5_000),
            Turn("TURN-3", 5_000),
            Turn("TURN-4", 5_000));

        lead.Should().Contain(
            "of this agent's OWN",
            "dropping the reviewer's own turns is survivable; dropping them quietly is the defect");
        lead.Should().Contain(
            "review.md",
            "the notice has to point at the authoritative body, or a reader learns only that something is "
                + "missing and not where to look");
        logs.WarningCount("did not fit the notes artifact").Should().Be(
            1,
            "the file disclosure is for whoever opens the file; losing reviewer output also has to reach the "
                + "operator, who is not going to open it");
    }

    /// <summary>
    /// The consequence of tier 1 being CHRONOLOGICAL, which reads backwards until you count the copies: when
    /// the agent's own turns overrun the budget between themselves, the turn that drops is the LAST one — the
    /// synthesis, the actual review — while the earlier mid-flight chatter survives.
    /// <para>
    /// That is deliberate and must not be "fixed". The synthesis has a second verbatim copy in
    /// <c>review.md</c> in this same notes dir; the mid-flight turns have no copy anywhere once the hosted
    /// conversation is discarded at <c>DeepLinkRetentionHours</c>. Spending a unique copy to protect a
    /// redundant one is the trade this ordering refuses.
    /// </para>
    /// <para>
    /// Pinned because it is the single most reversible-looking line in the selection code: the next reader to
    /// notice "the review itself is dropped first" will read it as a bug. This test is what tells them it is
    /// not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task On_overflow_the_synthesis_is_sacrificed_before_the_turns_that_have_no_other_copy()
    {
        var lead = await LeadFileAsync(
            Turn("MIDFLIGHT-EARLIEST", 5_000),
            Turn("MIDFLIGHT-SECOND", 5_000),
            Turn("THE-SYNTHESIS", 5_000));

        lead.Should().Contain(
            "MIDFLIGHT-EARLIEST",
            "the mid-flight turns exist nowhere else once the host's 24h sweep runs");
        lead.Should().Contain("MIDFLIGHT-SECOND");
        lead.Should().NotContain(
            "THE-SYNTHESIS",
            "the synthesis is the turn that CAN be spared, because review.md holds it verbatim — if it "
                + "survived here while a mid-flight turn was dropped, the ordering would have been reversed "
                + "and the daemon would be protecting the copy it already has");
        lead.Should().Contain(
            "of this agent's OWN",
            "and the sacrifice is still announced — deliberate is not the same as silent");
    }

    /// <summary>
    /// The accounting, which is the half of this defect that survived #53. The content is retained now; what
    /// nothing recorded was HOW MUCH there had been.
    /// <para>
    /// The daemon drives exactly two turns. A thread carrying five means the lead agent spoke three times on
    /// the host's initiative, and those turns exist in no daemon-side record at all — not the provisional
    /// checkpoint, not the review artifact. Before this line, a healthy run logged nothing about them: the
    /// only turn counters in the whole daemon fired when something had been DROPPED, so "the lead said
    /// nothing mid-review" and "the lead said three things and they happened to fit" produced byte-identical
    /// logs. An operator could not tell the two apart, and neither could an audit.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_read_reports_the_turn_count_even_when_nothing_was_dropped()
    {
        var (_, logs) = await LeadFileAndLogsAsync(
            Turn("THE-DAEMONS-BRIEF", 400, role: "user"),
            Turn("PROVISIONAL", 1_549),
            Turn(SparkBlocker, 600),
            Turn(DeclinedRationale, 421),
            Turn(ThirdOrphan, 601),
            Turn("SYNTHESIS", 5_491));

        var line = logs.MessagesAtLevel(LogLevel.Information)
            .Should().ContainSingle(m => m.Contains("assistant turn(s)", StringComparison.Ordinal)).Subject;

        line.Should().Contain(
            "held 5 assistant turn(s)",
            "five is the number that makes the gap visible — the daemon holds its own copy of two of them, so "
                + "a reader can see three turns exist that it never captured");
        line.Should().Contain(
            "of 6 message(s)",
            "the brief is a message but not a turn, so the two numbers must differ here — equal counts would "
                + "mean the line is reporting messages and would read as 6 turns on a thread that had 5");
        line.Should().Contain(
            "0 dropped",
            "the count has to be reported on the HEALTHY path; a line that only appears when the budget bit "
                + "would answer 'did anything get cut' while leaving 'how much was there' unanswerable");
    }

    /// <summary>
    /// The negative control for the test above: a thread where the daemon drove everything reports two, so
    /// the count is a real measurement of the transcript rather than a constant that happens to look right.
    /// </summary>
    [Fact]
    public async Task A_thread_with_no_mid_review_turns_reports_only_the_two_the_daemon_drove()
    {
        var (_, logs) = await LeadFileAndLogsAsync(
            Turn("PROVISIONAL", 1_549),
            Turn("SYNTHESIS", 5_491));

        logs.MessagesAtLevel(LogLevel.Information)
            .Should().ContainSingle(m => m.Contains("assistant turn(s)", StringComparison.Ordinal))
            .Subject.Should().Contain(
                "held 2 assistant turn(s)",
                "two is the floor — a review that reaches this artifact always has a provisional and a "
                    + "synthesis, so this is what 'the lead said nothing in between' looks like");
    }

    /// <summary>
    /// The count is reported ONCE per run, for the root conversation, however many specialists were
    /// dispatched.
    /// <para>
    /// The transcript helper is shared by the lead and every delegate, so an unconditional log there is one
    /// line per agent — six on a five-delegate review. That matters more than tidiness: this count exists to
    /// be noticed, and a number that arrives as routine per-agent chatter is filtered out within a week,
    /// which would rebuild the blindness it was added to end. It is also the only line that would be
    /// meaningless per-delegate: a specialist's thread has no daemon-driven turns to compare against, so
    /// "held N assistant turns" there answers no question anyone is asking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_turn_count_is_reported_once_per_run_not_once_per_dispatched_agent()
    {
        var logs = new CapturingLogger<ReviewNotesArtifactBuilder>();
        var builder = new ReviewNotesArtifactBuilder(
            new FakeTranscripts(
                root: [Turn("PROVISIONAL", 1_549), Turn(SparkBlocker, 600), Turn("SYNTHESIS", 5_491)],
                descendant: [Turn("A DELEGATE'S OWN ANSWER", 500)]),
            logs);

        _ = await builder.BuildAsync(
            NewRun(),
            NewRepo(),
            "PRs/nova-5505268",
            NewContext(Node("agent-1", "security"), Node("agent-2", "performance"), Node("agent-3", "tests")),
            CancellationToken.None);

        logs.MessagesAtLevel(LogLevel.Information)
            .Where(m => m.Contains("assistant turn(s)", StringComparison.Ordinal))
            .Should().ContainSingle(
                "three delegates were dispatched and each had a readable transcript, so a helper-level log "
                    + "would have produced four lines")
            .Which.Should().Contain(
                "lead reviewer (primary)",
                "and the one line has to be the ROOT conversation's — that is the only thread where the "
                    + "daemon's own two turns are the right thing to compare against");
    }
}
