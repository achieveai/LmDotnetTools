using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises a <em>nested-prompt</em> sub-agent fan-out: the parent's scripted turn emits an
/// <c>Agent</c> tool call whose <c>prompt</c> argument carries an embedded
/// <c>&lt;|instruction_start|&gt;…&lt;|instruction_end|&gt;</c> instruction chain. The sub-agent is
/// backed by the embedded-chain handler (<see cref="TestSseMessageHandler"/> /
/// <see cref="AnthropicTestSseMessageHandler"/>), so it consumes that nested chain to call exactly
/// one tool (<c>calculate</c>) and then reply "hi from agent". Under the synchronous Agent model,
/// the sub-agent's final text comes back as the <c>Agent</c> tool result.
/// </summary>
/// <remarks>
/// The foreground case keeps the parent scripted via <see cref="ScriptedSseResponder"/> (role dispatch,
/// no embedded tags) and only the sub-agent embedded-chain-driven. The background case drives the
/// parent by an embedded chain as well — the nested-marker depth walk in
/// <c>InstructionChainParser.ExtractInstructionChain</c> selects the outer chain's own closing tag —
/// because the parent's chain bookkeeping is exactly what that case pins.
/// </remarks>
public sealed class SubAgentEmbeddedChainTests
{
    // The nested prompt handed to the sub-agent via the Agent tool's `prompt` argument.
    // Turn 1: call `calculate` (a tool inherited from the parent). Turn 2: reply with text, which
    // ends the synchronous run and becomes the Agent tool result.
    private const string InnerChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"sub-tool","id_message":"Sub-agent uses calculate","messages":[{"tool_call":[{"name":"calculate","args":{"a":2,"operation":"add","b":3}}]}]},{"id":"sub-text","id_message":"Sub-agent replies","messages":[{"text":"hi from agent"}]}]}<|instruction_end|>""";

    private const string WrapUpText = "Spawned alpha and beta in the background.";
    private const string AlphaText = "Alpha reporting: I found three fresh AI papers today.";
    private const string BetaText = "Beta reporting: 40 + 2 = 42.";

    // The parent's OWN chain (#711 repro): step 1 spawns two background children whose prompts are
    // nested chains (alpha: one text turn; beta: calculate then text), step 2 is the wrap-up text.
    private const string BackgroundParentChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"spawn-two","id_message":"Spawn two background workers","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","name":"alpha","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"a1\",\"messages\":[{\"text\":\"Alpha reporting: I found three fresh AI papers today.\"}]}]}<|instruction_end|>"}},{"name":"Agent","args":{"subagent_type":"general-purpose","name":"beta","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"b1\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":40,\"operation\":\"add\",\"b\":2}}]}]},{\"id\":\"b2\",\"messages\":[{\"text\":\"Beta reporting: 40 + 2 = 42.\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent-done","id_message":"Wrap up","messages":[{"text":"Spawned alpha and beta in the background."}]}]}<|instruction_end|>""";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_passes_nested_chain_and_subagent_uses_tool_then_replies(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "general-purpose", prompt = InnerChain }))
            .Turn(t => t.Text("Summary: the sub-agent replied 'hi from agent'."))
            .Build();

        var handler = providerMode == "test-anthropic" ? responder.AsAnthropicHandler() : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(
            handler,
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(providerMode, loggerFactory)
        );

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-embedded-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("delegate to the embedded-chain sub-agent");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        // Parent delegated via the Agent tool.
        frames.ToolCallNames().Should().Contain("Agent");

        // Synchronous Agent: the sub-agent ran its nested chain (calculate -> text) and its final
        // text comes back as the Agent tool result — proving the embedded chain executed end to end.
        frames
            .ToolCallResults()
            .Should()
            .Contain(
                r => r.Contains("hi from agent", StringComparison.Ordinal),
                "the Agent tool result is the sub-agent's final text from the nested instruction chain"
            );

        frames.ConcatText().Should().Contain("the sub-agent replied");

        responder.RemainingTurns["parent"].Should().Be(0);
    }

    /// <summary>
    /// Regression guard for #711. A background child's completion reaches the parent as a
    /// <c>NotifyMessage</c> whose envelope relays the child's raw task — here a full nested chain — on
    /// a user turn. The mock's conversation analyzer must keep driving the parent from the parent's OWN
    /// chain: the wrap-up (step 2) is emitted, and neither child's scripted output is ever generated at
    /// the parent's level. Both children finish in milliseconds, so a completion notification usually
    /// lands before the wrap-up generation; but the guard is ordering-independent, because a
    /// notification-triggered run that adopted the child's chain would replay the child's text as the
    /// parent's either way.
    /// </summary>
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Background_completion_notifications_do_not_replay_child_chains_as_parent(string providerMode)
    {
        // The PARENT runs on the real embedded-chain handler too (not the scripted responder): the
        // conversation analyzer that picks its next scripted step is the seam under test.
        var builder = new ScriptedBuilder(
            CreateEmbeddedChainHandler(providerMode, NullLoggerFactory.Instance),
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(providerMode, loggerFactory)
        );

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-bgchain-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync(BackgroundParentChain);
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        // Live frames prove the spawn happened; the exact call count is asserted on the persisted
        // history below, because the real handler streams the long nested-prompt args across many
        // tool_call_update frames and ToolCallNames() counts every one of them.
        frames.ToolCallNames().Should().Contain("Agent", "step 1 of the parent's chain spawns the children");

        // The wrap-up belongs to the spawning run itself: step 2 follows the tool results in the same
        // run, regardless of whether a completion notification has already been injected into it.
        frames
            .ConcatText()
            .Should()
            .Contain(WrapUpText, "the parent's chain must resume at step 2 after the spawn tool results");

        using var http = factory.CreateClient();
        var history = await WaitForSettledParentHistoryAsync(
            http,
            threadId,
            expectedNotifications: 2,
            TimeSpan.FromSeconds(30)
        );

        var assistantItems = history.Where(IsAssistant).ToList();

        assistantItems
            .Count(m =>
                m.GetProperty("messageType").GetString() == "ToolCallMessage"
                && m.GetProperty("messageJson")
                    .GetString()!
                    .Contains("\"function_name\":\"Agent\"", StringComparison.Ordinal)
            )
            .Should()
            .Be(2, "step 1 of the parent's chain spawns both background children exactly once");

        var assistantTexts = assistantItems
            .Where(m => m.GetProperty("messageType").GetString() == "TextMessage")
            .Select(TextOf)
            .ToList();

        assistantTexts.Should().Contain(WrapUpText, "the wrap-up is persisted as the parent's own assistant text");

        assistantTexts
            .Should()
            .NotContain(
                t => t.Contains(AlphaText, StringComparison.Ordinal) || t.Contains(BetaText, StringComparison.Ordinal),
                "a child's scripted text must never be generated at the parent's level; assistant texts were: {0}",
                string.Join(" | ", assistantTexts)
            );

        assistantItems
            .Should()
            .NotContain(
                m =>
                    m.GetProperty("messageJson")
                        .GetString()!
                        .Contains("\"function_name\":\"calculate\"", StringComparison.Ordinal),
                "the calculate call is beta's own chain step, never the parent's"
            );
    }

    /// <summary>
    /// Polls <c>GET /api/conversations/{threadId}/messages</c> until the parent's persisted history holds
    /// <paramref name="expectedNotifications"/> <c>NotifyMessage</c> items AND an assistant
    /// <c>TextMessage</c> after the last of them — i.e. the parent has answered the final completion
    /// notification, so every notification-triggered generation has landed. Condition-based, bounded by
    /// <paramref name="timeout"/> as a safety net; each attempt is a real awaited round-trip.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> WaitForSettledParentHistoryAsync(
        HttpClient http,
        string threadId,
        int expectedNotifications,
        TimeSpan timeout
    )
    {
        using var cts = new CancellationTokenSource(timeout);
        var lastBody = "<none>";

        while (!cts.IsCancellationRequested)
        {
            using var response = await http.GetAsync($"/api/conversations/{threadId}/messages", cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            lastBody = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(lastBody);
            var items = doc.RootElement.EnumerateArray().Select(m => m.Clone()).ToList();

            var lastNotifyIndex = items.FindLastIndex(IsNotification);
            var answered =
                lastNotifyIndex >= 0
                && items
                    .Skip(lastNotifyIndex + 1)
                    .Any(m => IsAssistant(m) && m.GetProperty("messageType").GetString() == "TextMessage");

            if (items.Count(IsNotification) >= expectedNotifications && answered)
            {
                return items;
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"Parent history did not settle ({expectedNotifications} completion notifications each answered) "
                + $"within {timeout}. Last messages body: {lastBody}"
        );
    }

    private static bool IsNotification(JsonElement persisted) =>
        persisted.GetProperty("messageType").GetString() == "NotifyMessage";

    private static bool IsAssistant(JsonElement persisted) =>
        string.Equals(persisted.GetProperty("role").GetString(), "Assistant", StringComparison.OrdinalIgnoreCase);

    private static string TextOf(JsonElement persisted)
    {
        using var doc = JsonDocument.Parse(persisted.GetProperty("messageJson").GetString()!);
        return doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    private static SubAgentOptions BuildSubAgentOptions(string providerMode, ILoggerFactory loggerFactory)
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["general-purpose"] = new SubAgentTemplate
            {
                Name = "EmbeddedChainSub",
                // Marker only — the sub-agent's behavior comes from the embedded chain in its
                // prompt, not from role dispatch.
                SystemPrompt = "You are an embedded-chain sub-agent.",
                AgentFactory = () => BuildEmbeddedChainAgent(providerMode, loggerFactory),
                MaxTurnsPerRun = 5,
            },
        };

        return new SubAgentOptions { Templates = templates, MaxConcurrentSubAgents = 5 };
    }

    // The embedded-chain test handler for the requested wire (parses
    // <|instruction_start|>…<|instruction_end|> from the request), mirroring the sample's
    // DefaultTestAgentBuilder. No inter-chunk delay: the handlers' human-paced default (500 ms) is
    // applied per 10-CHARACTER slice of tool-call arguments on the OpenAI wire, so the two nested
    // Agent prompts alone would take ~30 s to stream and starve the harness timeout.
    private static HttpMessageHandler CreateEmbeddedChainHandler(string providerMode, ILoggerFactory loggerFactory)
    {
        return providerMode == "test-anthropic"
            ? new AnthropicTestSseMessageHandler(loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>())
            {
                ChunkDelayMs = 0,
            }
            : new TestSseMessageHandler(loggerFactory.CreateLogger<TestSseMessageHandler>()) { ChunkDelayMs = 0 };
    }

    // Builds a sub-agent backed by the embedded-chain test handler, mirroring the sample's
    // test-mode agent construction (Program.CreateTestAgent / CreateAnthropicTestAgent).
    private static IStreamingAgent BuildEmbeddedChainAgent(string providerMode, ILoggerFactory loggerFactory)
    {
        var handler = CreateEmbeddedChainHandler(providerMode, loggerFactory);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test-mode/v1") };

        if (providerMode == "test-anthropic")
        {
            var anthropicClient = new AnthropicClient(
                httpClient,
                baseUrl: "http://test-mode/v1",
                logger: loggerFactory.CreateLogger<AnthropicClient>()
            );
            return new AnthropicAgent(
                "MockAnthropicSub",
                anthropicClient,
                loggerFactory.CreateLogger<AnthropicAgent>()
            );
        }

        var openClient = new OpenClient(
            httpClient,
            "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<OpenClient>()
        );
        return new OpenClientAgent("MockSub", openClient, loggerFactory.CreateLogger<OpenClientAgent>());
    }
}
