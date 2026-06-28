using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     runId==null leak. GPT-5.5 (Copilot) routes through the Responses provider, which constructs
///     messages itself and never calls <c>WithIds(options)</c> — so unlike <c>OpenAgent</c> and
///     <c>AnthropicAgent</c>, it stamped only the GenerationId and left <c>RunId</c>/<c>ParentRunId</c>/
///     <c>ThreadId</c> null on every emitted message. The client merge key is
///     (kind, runId, generationId, messageOrderIdx); a uniformly-empty runId segment is a latent
///     identity bug masked only by per-turn generationId uniqueness. Every message emitted while
///     processing a run must carry the run's ids from <see cref="GenerateReplyOptions"/>.
/// </summary>
public sealed class OpenAiResponsesAgentRunIdTests
{
    private sealed class ScriptedClient(IReadOnlyList<ResponseEvent> events) : IOpenAiResponsesClient
    {
        public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
            ResponseCreateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (var ev in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }

    private static JsonElement El(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>A single response that exercises reasoning, text, a function call, and usage.</summary>
    private static ResponseEvent[] MixedStream() =>
        [
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                Response = El("{\"id\":\"resp_OPAQUE\"}"),
            },
            new ResponseReasoningSummaryTextDeltaEvent
            {
                Type = ResponseEventTypes.ReasoningSummaryTextDelta,
                OutputIndex = 0,
                Delta = "think",
            },
            new ResponseReasoningSummaryTextDoneEvent
            {
                Type = ResponseEventTypes.ReasoningSummaryTextDone,
                OutputIndex = 0,
                Text = "think done",
            },
            new ResponseOutputTextDeltaEvent
            {
                Type = ResponseEventTypes.OutputTextDelta,
                OutputIndex = 1,
                Delta = "Hello",
            },
            new ResponseOutputTextDoneEvent
            {
                Type = ResponseEventTypes.OutputTextDone,
                OutputIndex = 1,
                Text = "Hello",
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                OutputIndex = 2,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_1\",\"name\":\"add\"}"),
            },
            new ResponseFunctionCallArgumentsDoneEvent
            {
                Type = ResponseEventTypes.FunctionCallArgumentsDone,
                ItemId = "item-1",
                Arguments = "{\"a\":1}",
            },
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCompleted,
                Response = El("{\"id\":\"resp_OPAQUE\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}"),
            },
        ];

    /// <summary>A function call surfaced ONLY via the terminal output_item.done fallback (no
    /// function_call_arguments.done) — the path used by backends that rotate the per-event item_id.</summary>
    private static ResponseEvent[] ToolCallViaOutputItemDoneOnly() =>
        [
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                Response = El("{\"id\":\"resp_OPAQUE\"}"),
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_1\",\"name\":\"add\"}"),
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = El(
                    "{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_1\",\"name\":\"add\",\"arguments\":\"{\\\"a\\\":1}\"}"
                ),
            },
        ];

    /// <summary>A reasoning part surfaced via the output_item.done "reasoning" branch (summary array),
    /// a distinct code path from the reasoning_summary_text delta/done events.</summary>
    private static ResponseEvent[] ReasoningViaOutputItemDone() =>
        [
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                Response = El("{\"id\":\"resp_OPAQUE\"}"),
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = El("{\"type\":\"reasoning\",\"summary\":[{\"text\":\"deep thought\"}]}"),
            },
        ];

    /// <summary>Text deltas with NO finalizing output_text.done — a truncated/aborted stream. The
    /// non-streaming <c>GenerateReplyAsync</c> then synthesizes a leftover <c>TextMessage</c> at its
    /// OWN fix site (distinct from the streaming <c>StampRunIds</c> wrapper), which must still stamp
    /// the run ids.</summary>
    private static ResponseEvent[] TextDeltasOnlyStream() =>
        [
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                Response = El("{\"id\":\"resp_OPAQUE\"}"),
            },
            new ResponseOutputTextDeltaEvent
            {
                Type = ResponseEventTypes.OutputTextDelta,
                OutputIndex = 0,
                Delta = "Hello",
            },
        ];

    [Fact]
    public async Task Every_emitted_message_carries_the_runs_run_id()
    {
        const string runId = "run-123";
        const string parentRunId = "parent-456";
        const string threadId = "thread-789";
        const string runGenerationId = "run-gen-ABC";

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(MixedStream()));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions
            {
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                GenerationId = runGenerationId,
            }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        messages.Should().NotBeEmpty("the scripted stream emits reasoning, text, a tool call, and usage");
        messages
            .Should()
            .OnlyContain(
                m => m.RunId == runId,
                "every Responses-path message emitted during a run must carry the run's RunId "
                    + "(parity with OpenAgent/AnthropicAgent .WithIds(options))"
            );
        messages
            .Should()
            .OnlyContain(m => m.ThreadId == threadId, "WithIds stamps ThreadId on every emitted type");
        messages
            .Where(m => m is not UsageMessage)
            .Should()
            .OnlyContain(
                m => m.ParentRunId == parentRunId,
                "WithIds stamps ParentRunId on every emitted type except UsageMessage (which carries none)"
            );

        // BUG H1: the run's GenerationId must be preserved on the finalized tool call (folded in from
        // a former near-duplicate test, asserted against the same MixedStream run).
        messages
            .OfType<ToolsCallMessage>()
            .Single()
            .GenerationId.Should()
            .Be(runGenerationId, "the BUG H1 GenerationId behavior must be preserved");
    }

    [Fact]
    public async Task Tool_call_via_output_item_done_fallback_carries_run_ids()
    {
        const string runId = "run-123";
        const string parentRunId = "parent-456";
        const string threadId = "thread-789";
        const string runGenerationId = "run-gen-ABC";

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(ToolCallViaOutputItemDoneOnly()));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions
            {
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                GenerationId = runGenerationId,
            }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        var toolCall = messages.OfType<ToolsCallMessage>().Single();
        toolCall.RunId.Should().Be(runId);
        toolCall.ParentRunId.Should().Be(parentRunId);
        toolCall.ThreadId.Should().Be(threadId);
    }

    [Fact]
    public async Task Reasoning_via_output_item_done_carries_run_ids()
    {
        const string runId = "run-123";
        const string parentRunId = "parent-456";
        const string threadId = "thread-789";
        const string runGenerationId = "run-gen-ABC";

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(ReasoningViaOutputItemDone()));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions
            {
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                GenerationId = runGenerationId,
            }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        var reasoning = messages.OfType<ReasoningMessage>().Single();
        reasoning.RunId.Should().Be(runId);
        reasoning.ParentRunId.Should().Be(parentRunId);
        reasoning.ThreadId.Should().Be(threadId);
    }

    [Fact]
    public async Task GenerateReplyAsync_leftover_text_carries_run_ids()
    {
        // The non-streaming path has its OWN fix site: when the stream emits text deltas but no
        // finalizing output_text.done (truncated/aborted), GenerateReplyAsync synthesizes the leftover
        // TextMessage itself and stamps it directly with .WithIds(options) — separate from the
        // streaming StampRunIds wrapper the other tests exercise. This pins that path against the
        // null-RunId identity bug it is most prone to.
        const string runId = "run-123";
        const string parentRunId = "parent-456";
        const string threadId = "thread-789";

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(TextDeltasOnlyStream()));
        var result = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions
            {
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
            }
        );

        var text = result.OfType<TextMessage>().Single();
        text.RunId.Should().Be(runId);
        text.ParentRunId.Should().Be(parentRunId);
        text.ThreadId.Should().Be(threadId);
    }

    [Fact]
    public async Task Run_id_stays_null_when_options_advertise_none()
    {
        // No regression for the no-run case: when the caller supplies no RunId, messages keep a null
        // RunId (which the serializer omits) rather than inventing one.
        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(MixedStream()));
        var stream = await agent.GenerateReplyStreamingAsync([new TextMessage { Role = Role.User, Text = "go" }]);

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        messages.Should().NotBeEmpty();
        messages.Should().OnlyContain(m => m.RunId == null);
    }
}
