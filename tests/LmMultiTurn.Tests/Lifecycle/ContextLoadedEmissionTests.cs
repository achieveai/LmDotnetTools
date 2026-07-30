using System.Security.Cryptography;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// Covers what <c>context_loaded</c> promises: it describes context the model actually received, in
/// the exact bytes it received, and says so once per source.
/// </summary>
/// <remarks>
/// The distinction these tests defend is between <i>discovered</i> and <i>delivered</i>. Context can
/// be discovered and then queued, cancelled, superseded, or rediscovered without ever reaching a
/// model, so the event is published from the request snapshot on its way out rather than from the
/// discovery that produced it. Everything below is written against that seam: a request goes in, and
/// the assertion is about what a subscriber may conclude from what came out.
/// </remarks>
public class ContextLoadedEmissionTests
{
    private const string ClaudeMdPath = "/workspace/target/CLAUDE.md";
    private const string ClaudeMdBody = "# Project\nUse tabs.";

    #region What the event describes

    [Fact]
    public async Task TheEventCarriesTheBlockExactlyAsTheRequestCarriesIt()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var block = Rendered(ClaudeMdPath, ClaudeMdBody);

        await agent.ReportAsync(assignment, [SystemPrompt("You are helpful.\n\n" + block.Text)]);

        publisher.EventTypes.Should().Contain(LifecycleEventTypes.ContextLoaded);

        var payload = publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle()
            .Subject;

        payload.RunId.Should().Be(assignment.RunId);
        payload.GenerationId.Should().Be(assignment.GenerationId);
        payload.RenderedText.Should().Be(block.Text, "the event reports the bytes that were sent");
        payload.RenderedByteCount.Should().Be(Encoding.UTF8.GetByteCount(block.Text));
        payload.RenderedHash.Should().Be(Sha256Hex(block.Text));
    }

    [Fact]
    public async Task ASourceInTheSystemPromptIsDescribedAsABootSeed()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(
            assignment,
            [SystemPrompt(Rendered(@"\workspace\target\docs\CLAUDE.md", ClaudeMdBody).Text)]);

        var source = SingleSource(publisher);
        source.Phase.Should().Be(LifecycleContextPhases.Boot);
        source.DiscoveryKind.Should().Be(RenderedContextBlock.ContextFileKind);
        source.NormalizedPath.Should()
            .Be("/workspace/target/docs/CLAUDE.md", "separators are normalized so two hosts agree");
        source.Name.Should().Be("CLAUDE.md");
        source.DedupIdentity.Should()
            .Be($"{RenderedContextBlock.ContextFileKind}:/workspace/target/docs/CLAUDE.md");
        source.WasTruncated.Should().BeFalse();
        source.RenderedByteCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ASourceDeliveredAfterTheFirstTurnIsDescribedAsMidSession()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(
            assignment,
            [
                SystemPrompt("You are helpful."),
                NotifyMessage.Create(
                    NotifyKinds.ContextDiscovery,
                    detail: Rendered(ClaudeMdPath, ClaudeMdBody, phase: LifecycleContextPhases.MidSession).Text,
                    generationId: "notify-1"),
            ]);

        SingleSource(publisher).Phase.Should().Be(LifecycleContextPhases.MidSession);
    }

    [Fact]
    public async Task ATruncatedSourceSaysTheModelSawLessThanTheFileHolds()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(
            assignment,
            [SystemPrompt(Rendered(ClaudeMdPath, ClaudeMdBody, truncated: true).Text)]);

        SingleSource(publisher).WasTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task SeveralNewSourcesInOneRequestAreOneEventHashedOverTheConcatenation()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var claude = Rendered(ClaudeMdPath, ClaudeMdBody);
        var agents = Rendered("/workspace/target/AGENTS.md", "# Agents\nBe brief.");

        await agent.ReportAsync(assignment, [SystemPrompt(claude.Text + "\n" + agents.Text)]);

        var payload = publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle("one request carrying two sources is one delivery")
            .Subject;

        payload.Sources.Select(s => s.NormalizedPath).Should()
            .Equal([ClaudeMdPath, "/workspace/target/AGENTS.md"], "sources follow request order");

        // No separator: the hash covers the blocks and nothing the model was never sent. The
        // newline between them above is part of the prompt, not part of either block.
        payload.RenderedText.Should().Be(claude.Text + agents.Text);
        payload.RenderedHash.Should().Be(Sha256Hex(claude.Text + agents.Text));
    }

    #endregion

    #region Delivered, not discovered

    [Fact]
    public async Task ARequestWithoutAContextBlockPublishesNothing()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(
            assignment,
            [SystemPrompt("You are helpful."), Text("Read CLAUDE.md and tell me the rules.")]);

        publisher.EventTypes.Should().NotContain(LifecycleEventTypes.ContextLoaded);
    }

    [Fact]
    public async Task ARunThatNeverDispatchedPublishesNothing()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        // Discovery happened — the block exists — but the run was cancelled before any request
        // carried it, so nothing was ever handed to a model.
        _ = Rendered(ClaudeMdPath, ClaudeMdBody);

        var assignment = await agent.StartAsync();
        await agent.CompleteAsync(assignment, outcome: LifecycleRunOutcomes.Cancelled);

        publisher.EventTypes.Should().NotContain(LifecycleEventTypes.ContextLoaded);
    }

    [Fact]
    public async Task AProviderPromptWithNoBlockPublishesNothing()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportPromptAsync(assignment, "user: summarize the repo");

        publisher.EventTypes.Should().NotContain(LifecycleEventTypes.ContextLoaded);
    }

    [Fact]
    public async Task AProviderPromptCarryingABlockIsReportedLikeAMessageRequest()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var block = Rendered(ClaudeMdPath, ClaudeMdBody, phase: LifecycleContextPhases.MidSession);

        await agent.ReportPromptAsync(assignment, "user: hello\n\n" + block.Text);

        var payload = publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle()
            .Subject;

        payload.RenderedText.Should().Be(block.Text);
        payload.Sources.Should().ContainSingle().Which.Phase.Should().Be(LifecycleContextPhases.MidSession);
    }

    #endregion

    #region Once per source

    [Fact]
    public async Task ABootSeedRidingEveryRequestIsReportedOnlyOnTheFirstOne()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var systemPrompt = SystemPrompt("You are helpful.\n\n" + Rendered(ClaudeMdPath, ClaudeMdBody).Text);

        await agent.ReportAsync(assignment, [systemPrompt]);
        await agent.ReportAsync(assignment, [systemPrompt, Text("turn two")], generationId: "gen-2");
        await agent.ReportAsync(assignment, [systemPrompt, Text("turn three")], generationId: "gen-3");

        publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle("the same block in every request is one delivery, not one per turn");
    }

    [Fact]
    public async Task TheSamePathRediscoveredWithNewContentIsNotASecondDelivery()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(assignment, [SystemPrompt(Rendered(ClaudeMdPath, ClaudeMdBody).Text)]);
        await agent.ReportAsync(
            assignment,
            [SystemPrompt(Rendered(ClaudeMdPath, "# Project\nUse spaces after all.").Text)],
            generationId: "gen-2");

        publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle("identity is the source, not its bytes");
    }

    [Fact]
    public async Task ANewSourceArrivingLaterIsReportedWithoutRepeatingTheOldOne()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var claude = Rendered(ClaudeMdPath, ClaudeMdBody);
        var agents = Rendered("/workspace/target/AGENTS.md", "# Agents\nBe brief.");

        await agent.ReportAsync(assignment, [SystemPrompt(claude.Text)]);
        await agent.ReportAsync(
            assignment,
            [SystemPrompt(claude.Text + agents.Text)],
            generationId: "gen-2");

        var payloads = publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded);
        payloads.Should().HaveCount(2);
        payloads[0].Sources.Should().ContainSingle().Which.NormalizedPath.Should().Be(ClaudeMdPath);
        payloads[1].Sources.Should()
            .ContainSingle("the block already reported is not re-announced")
            .Which.NormalizedPath.Should()
            .Be("/workspace/target/AGENTS.md");
        payloads[1].RenderedText.Should().Be(agents.Text);
    }

    #endregion

    #region Reading a request back

    [Fact]
    public async Task ATagThatMerelyStartsTheSameWayIsNotAContextBlock()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        await agent.ReportAsync(
            assignment,
            [
                SystemPrompt(
                    "<context-discoveryX path=\"/nope.md\">body</context-discoveryX>\n"
                    + "Mention of <context-discovery in prose."),
            ]);

        publisher.EventTypes.Should().NotContain(LifecycleEventTypes.ContextLoaded);
    }

    [Fact]
    public async Task AnUnterminatedBlockReportsWhatWasWholeAndDoesNotThrow()
    {
        var (agent, publisher) = CreateWiredAgent("thread-1");
        await using var _agent = agent;

        var assignment = await agent.StartAsync();
        var whole = Rendered(ClaudeMdPath, ClaudeMdBody);

        await agent.ReportAsync(
            assignment,
            [SystemPrompt(whole.Text + "\n<context-discovery path=\"/truncated.md\">\nno close tag")]);

        var payload = publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle()
            .Subject;

        payload.RenderedText.Should().Be(whole.Text, "a mangled tag reports less, it does not fail the send");
    }

    [Fact]
    public void RenderingAndScanningAreTheSameGrammarInBothDirections()
    {
        const string awkwardPath = "dir\"quote/&amp<>.md";
        var written = RenderedContextBlock.Create(
            awkwardPath,
            ClaudeMdBody,
            truncated: true,
            LifecycleContextPhases.Boot);

        var read = RenderedContextBlock.Scan(
            "prefix\n" + written.Text + "\nsuffix",
            LifecycleContextPhases.Boot);

        read.Should().ContainSingle();
        read[0].Text.Should().Be(written.Text);
        read[0].NormalizedPath.Should().Be(awkwardPath, "escaping round-trips exactly");
        read[0].WasTruncated.Should().BeTrue();
        read[0].DedupIdentity.Should().Be(written.DedupIdentity);
        read[0].RenderedByteCount.Should().Be(written.RenderedByteCount);
    }

    #endregion

    #region Helpers

    private static RenderedContextBlock Rendered(
        string path,
        string content,
        bool truncated = false,
        string phase = LifecycleContextPhases.Boot) =>
        RenderedContextBlock.Create(path, content, truncated, phase);

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static TextMessage SystemPrompt(string text) => new() { Text = text, Role = Role.System };

    private static TextMessage Text(string text) => new() { Text = text, Role = Role.User };

    private static LifecycleContextSource SingleSource(RecordingLifecyclePublisher publisher) =>
        publisher.Payloads<ContextLoadedPayload>(LifecycleEventTypes.ContextLoaded)
            .Should()
            .ContainSingle()
            .Subject.Sources.Should()
            .ContainSingle()
            .Subject;

    private static (ContextProbeAgent Agent, RecordingLifecyclePublisher Publisher) CreateWiredAgent(
        string threadId)
    {
        var store = new InMemoryConversationStore();
        var publisher = new RecordingLifecyclePublisher();
        var agent = new ContextProbeAgent(
            threadId,
            new MultiTurnLifecycleServices { Publisher = publisher, LifecycleStore = store },
            store);
        return (agent, publisher);
    }

    /// <summary>
    /// A loop that hands requests to the context-reporting seam directly, so the tests observe what
    /// a subscriber sees rather than any one provider's dispatch plumbing.
    /// </summary>
    private sealed class ContextProbeAgent : MultiTurnAgentBase
    {
        public ContextProbeAgent(
            string threadId,
            MultiTurnLifecycleServices? services = null,
            IConversationStore? store = null)
            : base(threadId, store: store, lifecycleServices: services)
        {
        }

        public Task<RunAssignment> StartAsync(CancellationToken ct = default) =>
            StartRunAsync([], null, ct);

        /// <summary>Reports the request a message-list loop is about to dispatch.</summary>
        public Task ReportAsync(
            RunAssignment assignment,
            IEnumerable<IMessage> request,
            string? generationId = null,
            CancellationToken ct = default) =>
            ReportContextLoadedAsync(
                assignment.RunId,
                generationId ?? assignment.GenerationId,
                request,
                ct);

        /// <summary>Reports the prompt string a Codex/Copilot-shaped loop is about to dispatch.</summary>
        public Task ReportPromptAsync(
            RunAssignment assignment,
            string? prompt,
            CancellationToken ct = default) =>
            ReportContextLoadedAsync(assignment.RunId, assignment.GenerationId, prompt, ct: ct);

        public Task CompleteAsync(
            RunAssignment assignment,
            string? outcome = null,
            CancellationToken ct = default) =>
            CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                outcome: outcome,
                ct: ct);

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    #endregion
}
