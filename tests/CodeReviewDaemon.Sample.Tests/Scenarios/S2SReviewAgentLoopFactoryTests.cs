using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The S2S loop factory's resume seam (recursive-review completion barrier, Task 5). A review is now two
/// turns on ONE hosted conversation separated by the sub-agent completion barrier, so a review that is
/// picked up after the daemon restarted must rejoin the conversation it already minted instead of
/// provisioning a second one — a fresh conversation carries none of the review history the synthesis turn
/// reads, and orphans the deep-link already posted on the PR.
/// </summary>
public sealed class S2SReviewAgentLoopFactoryTests
{
    private static readonly PreparedReviewWorkspace Workspace = new(
        Leaf: "pr-118", WorkspaceId: "ws-118", HostDir: "/srv/checkouts/pr-118", PrId: "118");

    private static readonly AgentProfile Profile = new(
        Id: "review", Name: "Review Agent", SystemPrompt: "REVIEW METHODOLOGY",
        EnabledTools: null, EnabledBuiltInTools: []);

    private static S2SReviewAgentLoopFactory NewFactory(FakeHttpMessageHandler handler, HttpClient http) =>
        new(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                LmStreamingProviderId = "openai",
                LmStreamingModeId = "workspace-agent",
            },
            NullLoggerFactory.Instance);

    private static async Task<List<IMessage>> DriveAsync(
        AchieveAi.LmDotnetTools.LmMultiTurn.IMultiTurnAgent agent, string userText)
    {
        var collected = new List<IMessage>();
        var input = new UserInput([new TextMessage { Text = userText, Role = Role.User }]);
        await foreach (var message in agent.ExecuteRunAsync(input, CancellationToken.None))
        {
            collected.Add(message);
        }

        return collected;
    }

    /// <summary>
    /// The fake factory the executor tests run against can honour a per-call model id. THIS one cannot —
    /// provision carries no model field — so the executor's provenance recording is only as honest as this
    /// answer. Asserted against the real factory rather than the double, because a double that quietly
    /// diverges here is exactly how a false independence claim reaches a persisted artifact.
    /// </summary>
    [Fact]
    public void The_effective_model_is_the_configured_provider_never_the_requested_id()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("http://host/") };
        var factory = NewFactory(new FakeHttpMessageHandler(), http);

        factory.ResolveEffectiveModelId("anthropic/claude-opus-4").Should().Be("lmstreaming:openai");
        factory.ResolveEffectiveModelId(null).Should().Be("lmstreaming:openai");
    }

    /// <summary>
    /// And with no provider configured the transport names no model at all, which is unknown rather than
    /// a value — a caller must not persist it as a measurement or compare two of them for equality.
    /// </summary>
    [Fact]
    public void An_unconfigured_provider_names_no_effective_model()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("http://host/") };
        var factory = new S2SReviewAgentLoopFactory(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, LmStreamingModeId = "workspace-agent" },
            NullLoggerFactory.Instance);

        factory.ResolveEffectiveModelId("anthropic/claude-opus-4").Should().BeNull();
    }

    [Fact]
    public async Task Create_seeds_the_persisted_hosted_thread_and_never_provisions_on_resume()
    {
        // No provision route is registered: the fake handler answers an unrouted request with 501, so any
        // ProvisionAsync call fails this test outright.
        var handler = new FakeHttpMessageHandler().OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-2\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-resumed\",\"response\":{\"text\":\"resumed answer\"}}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };

        await using var agent = NewFactory(handler, http).Create(
            Profile, modelId: null, threadId: "review-run-7-a", reviewWorkspace: Workspace,
            resumeHostedThreadId: "thread-persisted");

        var messages = await DriveAsync(agent, "synthesize now");

        messages.Should().ContainSingle().Subject.Should().BeOfType<TextMessage>()
            .Which.Text.Should().Be("resumed answer");
        agent.ThreadId.Should().Be("thread-persisted");
        handler.Requests.Should().NotContain(
            r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal),
            "a resumed review rejoins its persisted conversation instead of minting a new one");
    }

    [Fact]
    public async Task Create_without_a_resume_thread_still_provisions_a_fresh_conversation()
    {
        var handler = new FakeHttpMessageHandler().OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"fresh answer\"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-fresh\"}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };

        await using var agent = NewFactory(handler, http).Create(
            Profile, modelId: null, threadId: "review-run-7-a", reviewWorkspace: Workspace);

        _ = await DriveAsync(agent, "review this PR");

        agent.ThreadId.Should().Be("thread-fresh");
        handler.Requests.Should().Contain(
            r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal),
            "the first turn of a new review still mints its conversation");
    }
}
