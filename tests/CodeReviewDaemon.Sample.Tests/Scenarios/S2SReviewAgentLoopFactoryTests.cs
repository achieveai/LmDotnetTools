using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
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

    private static S2SReviewAgentLoopFactory NewFactory(
        FakeHttpMessageHandler handler,
        HttpClient http,
        string subAgentModelId = "",
        ILoggerFactory? loggerFactory = null,
        string providerId = "openai",
        string reviewModelId = "openai") =>
        new(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new CodeReviewDaemonOptions
            {
                UseS2SReviewAgent = true,
                LmStreamingProviderId = providerId,
                LmStreamingModeId = "workspace-agent",
                ReviewModelId = reviewModelId,
                SubAgentModelId = subAgentModelId,
            },
            loggerFactory ?? NullLoggerFactory.Instance);

    /// <summary>A handler that answers the whole provision → send → status turn, so a test can read the
    /// PROVISION body — the only place the conversation's model (its provider id) is stated.</summary>
    private static FakeHttpMessageHandler ProvisioningHandler() =>
        new FakeHttpMessageHandler().OnCurrentReviewHostCapabilities()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-1\",\"response\":{\"text\":\"answer\"}}")
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-fresh\"}");

    /// <summary>The <c>providerId</c> the daemon named on the provision request — i.e. the model the hosted
    /// conversation will run on, since a Copilot-discovered provider id IS a model id on the review host.</summary>
    private static string? ProvisionedProviderId(FakeHttpMessageHandler handler) =>
        handler.Requests
            .Where(r => r.Body is not null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("providerId").GetString())
            .SingleOrDefault();

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
    /// What the executor persists as the model that ran. The requested id IS the answer now, because
    /// <c>Create</c> provisions the conversation with it as the provider id and a Copilot-discovered provider
    /// id is a model id on the review host. Asserted against the real factory rather than the double, because
    /// a double that quietly diverges here is exactly how a false provenance claim reaches a persisted
    /// artifact.
    /// <para>
    /// Both arms are pinned: a named model resolves to itself, and only an unnamed one falls back to the
    /// configured provider. Asserting the fallback alone would stay green against a factory that had gone
    /// back to discarding the request.
    /// </para>
    /// </summary>
    [Fact]
    public void The_effective_model_is_the_requested_id_and_falls_back_only_when_none_is_named()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("http://host/") };
        var factory = NewFactory(new FakeHttpMessageHandler(), http);

        factory.ResolveEffectiveModelId("anthropic/claude-opus-4").Should().Be(
            "lmstreaming:anthropic/claude-opus-4",
            "the requested id is what the conversation is provisioned on, so it is what ran");
        factory.ResolveEffectiveModelId(null).Should().Be("lmstreaming:openai");
        factory.ResolveEffectiveModelId("   ").Should().Be(
            "lmstreaming:openai", "blank is not a model, and Create falls back on it too");
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

    /// <summary>
    /// The half the executor gates its persisted model provenance on (<c>DaemonReviewStageExecutor</c>:
    /// <c>HonoursRequestedModelId ? modelOverride ?? run.ModelId : run.ModelId</c>). It answers <c>true</c>
    /// because <c>Create</c> forwards the id as the provisioned provider, so an escalated run is now
    /// checkpointed against the model that actually ran instead of the one it had already overflowed.
    /// <para>
    /// Answered the same either way the provider is configured: the property is about what <c>Create</c> does
    /// with the argument, not about whether a fallback exists. An unconfigured provider makes <c>Create</c>
    /// throw rather than run something else.
    /// </para>
    /// </summary>
    [Fact]
    public void The_per_call_model_id_is_honoured_because_the_provider_id_is_the_model()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler()) { BaseAddress = new Uri("http://host/") };
        var configured = NewFactory(new FakeHttpMessageHandler(), http);
        var unconfigured = new S2SReviewAgentLoopFactory(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, LmStreamingModeId = "workspace-agent" },
            NullLoggerFactory.Instance);

        configured.HonoursRequestedModelId.Should().BeTrue();
        unconfigured.HonoursRequestedModelId.Should().BeTrue();
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

    /// <summary>
    /// The test the original defect needed and did not have (#529). <c>CodeReviewDaemonOptions</c> declared
    /// <c>SubAgentModelId</c>, both live profiles set it to <c>gpt-5.6-sol</c>, and repo-wide NOTHING read
    /// it: the only test that named it asserted the DEFAULT was empty, which stays green whether or not a
    /// reader exists. So every review sub-agent silently ran the orchestrator's model and the two-model
    /// split both profiles advertise never happened.
    /// <para>
    /// This asserts on the provision REQUEST BODY, the daemon's last chance to state the choice: provision
    /// is the only moment the value can be set, because the host builds a thread's sub-agent options once,
    /// when it creates the agent. Deleting the <c>subAgentModelId:</c> argument in <c>Create</c> — the
    /// single line that reads the option — leaves every other test in this repository green and fails only
    /// this one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Create_puts_the_configured_SubAgentModelId_on_the_provision_wire()
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

        await using var agent = NewFactory(handler, http, subAgentModelId: "gpt-5.6-sol").Create(
            Profile, modelId: null, threadId: "review-run-7-a", reviewWorkspace: Workspace);

        _ = await DriveAsync(agent, "review this PR");

        var provision = handler.Requests
            .Should()
            .ContainSingle(r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal))
            .Subject;
        provision.Body.Should().Contain(
            "\"subAgentModelId\":\"gpt-5.6-sol\"",
            "the operator configured this model for the review sub-agents, and provision is the only call "
                + "that can carry it to the host");
    }

    /// <summary>
    /// The unconfigured default, and the half that keeps <c>""</c> meaning "inherit
    /// <c>ReviewModelId</c>" all the way onto the wire.
    /// <see cref="CodeReviewDaemonOptions.SubAgentModelId"/> defaults to the empty string rather than null,
    /// so this is the path EVERY daemon that has not opted in takes on every provision. It must send an
    /// explicit <c>null</c>: a host that stored <c>""</c> would hand each spawn a blank model id instead of
    /// leaving it to inherit the parent.
    /// </summary>
    [Fact]
    public async Task Create_sends_null_rather_than_a_blank_model_when_SubAgentModelId_is_unset()
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

        var provision = handler.Requests
            .Should()
            .ContainSingle(r => r.Body != null && r.Body.Contains("\"modeId\"", StringComparison.Ordinal))
            .Subject;
        provision.Body.Should().Contain("\"subAgentModelId\":null");
    }

    /// <summary>
    /// The escalation fix. A Copilot-discovered <c>providerId</c> IS the model id on the review host — it
    /// registers each discovered model as its own provider keyed by its raw id, persists the provisioned
    /// provider as thread metadata, and builds that thread's agent with
    /// <c>GenerateReplyOptions.ModelId = copilotModelInfo.Id</c> — so the model a caller names must reach the
    /// PROVISION request or it reaches nothing. It previously did not: the factory passed
    /// <c>LmStreamingProviderId</c> regardless, which is why the overflow-escalation ladder re-ran the very
    /// model it had just overflowed and called it an escalation.
    /// </summary>
    [Fact]
    public async Task Create_provisions_the_conversation_on_the_model_the_caller_named()
    {
        var handler = ProvisioningHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };

        await using var agent = NewFactory(handler, http, providerId: "gpt-5.6-luna").Create(
            Profile, modelId: "gpt-5.6-terra", threadId: "review-run-7-a-esc", reviewWorkspace: Workspace);

        _ = await DriveAsync(agent, "review this PR");

        ProvisionedProviderId(handler).Should().Be(
            "gpt-5.6-terra",
            "an escalation that does not change the model is not an escalation");
    }

    /// <summary>
    /// The spend-neutrality half, and the reason the fix is a null-coalesce rather than a rewrite: a caller
    /// that names no model still provisions on the configured provider, exactly as before. Every shipped S2S
    /// profile sets ReviewModelId == LmStreamingProviderId == gpt-5.6-luna, so the ordinary review is
    /// unchanged and only a deliberately different model (the escalation rung) can move it.
    /// </summary>
    [Fact]
    public async Task Create_falls_back_to_the_configured_provider_when_the_caller_names_no_model()
    {
        var handler = ProvisioningHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };

        await using var agent = NewFactory(handler, http, providerId: "gpt-5.6-luna").Create(
            Profile, modelId: null, threadId: "review-run-7-a", reviewWorkspace: Workspace);

        _ = await DriveAsync(agent, "review this PR");

        ProvisionedProviderId(handler).Should().Be("gpt-5.6-luna");
    }

    /// <summary>Blank is not a model. A whitespace-only id must fall back rather than reach the host, which
    /// rejects an empty provider id outright — that would turn an unset config into a failed review.</summary>
    [Fact]
    public async Task Create_falls_back_when_the_caller_names_a_blank_model()
    {
        var handler = ProvisioningHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };

        await using var agent = NewFactory(handler, http, providerId: "gpt-5.6-luna").Create(
            Profile, modelId: "   ", threadId: "review-run-7-a", reviewWorkspace: Workspace);

        _ = await DriveAsync(agent, "review this PR");

        ProvisionedProviderId(handler).Should().Be("gpt-5.6-luna");
    }

    /// <summary>
    /// Two configuration strings mean "the review model" and nothing made them agree: <c>ReviewModelId</c>
    /// becomes the run's ModelId and is what <c>ReviewProgressReporter</c> prints on the live
    /// <c>reviewing ({model})</c> line, while <c>LmStreamingProviderId</c> is what the conversation falls
    /// back to. They are equal in every shipped profile, so that line has told the truth by coincidence. It
    /// can no longer diverge quietly.
    /// </summary>
    [Fact]
    public void Constructing_the_factory_warns_when_the_two_review_model_settings_disagree()
    {
        var handler = ProvisioningHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        using var logs = new CapturingLoggerFactory();

        _ = NewFactory(
            handler, http, loggerFactory: logs, providerId: "gpt-5.6-luna", reviewModelId: "claude-opus-5");

        // ONE line must carry BOTH ids: an operator reading "these disagree" needs to see which two.
        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("gpt-5.6-luna").And.Contain("claude-opus-5");
    }

    /// <summary>The control: the shipped configuration agrees, so booting it must stay silent. Without this,
    /// a warning that fired unconditionally would pass the test above and cry wolf on every daemon.</summary>
    [Fact]
    public void Constructing_the_factory_is_silent_when_the_two_review_model_settings_agree()
    {
        var handler = ProvisioningHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        using var logs = new CapturingLoggerFactory();

        _ = NewFactory(
            handler, http, loggerFactory: logs, providerId: "gpt-5.6-luna", reviewModelId: "gpt-5.6-luna");

        logs.Capturing.MessagesAtLevel(LogLevel.Warning).Should().BeEmpty();
    }
}
