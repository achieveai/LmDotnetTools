using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;
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
}
