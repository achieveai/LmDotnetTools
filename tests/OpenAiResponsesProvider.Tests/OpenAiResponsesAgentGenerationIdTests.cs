using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     BUG H1 (Responses path). GPT-5.5 (Copilot) routes through the Responses provider, which stamped
///     the opaque provider response id (e.g. <c>resp_…</c>) as the message GenerationId instead of the
///     run's GenerationId. The client merges messages by (kind, runId, generationId, messageOrderIdx);
///     an opaque per-response id that never matches the run breaks tool-call grouping (the "pillbox"
///     bug). Every message emitted while processing a run must carry the run's GenerationId from
///     <see cref="GenerateReplyOptions.GenerationId"/>. The <c>WithIds</c> fix does not cover this path
///     because the Responses agent constructs messages itself and never calls <c>WithIds</c>.
/// </summary>
public sealed class OpenAiResponsesAgentGenerationIdTests
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

    private static ResponseEvent[] FunctionCallStreamWithResponseId(string opaqueResponseId) =>
        [
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCreated,
                Response = El($"{{\"id\":\"{opaqueResponseId}\"}}"),
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_1\",\"name\":\"add\"}"),
            },
            new ResponseFunctionCallArgumentsDoneEvent
            {
                Type = ResponseEventTypes.FunctionCallArgumentsDone,
                ItemId = "item-1",
                Arguments = "{\"a\":1}",
            },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = El(
                    "{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_1\",\"name\":\"add\",\"arguments\":\"{\\\"a\\\":1}\"}"
                ),
            },
            new ResponseLifecycleEvent
            {
                Type = ResponseEventTypes.ResponseCompleted,
                Response = El($"{{\"id\":\"{opaqueResponseId}\"}}"),
            },
        ];

    [Fact]
    public async Task Emitted_tool_call_carries_run_generation_id_not_opaque_response_id()
    {
        const string runGenerationId = "run-gen-ABC";
        const string opaqueResponseId = "resp_OPAQUE_xyz";

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(FunctionCallStreamWithResponseId(opaqueResponseId)));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions { GenerationId = runGenerationId }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        var toolCalls = messages.OfType<ToolsCallMessage>().ToList();
        toolCalls.Should().NotBeEmpty("the scripted stream emits a function call");
        toolCalls
            .Should()
            .OnlyContain(
                tc => tc.GenerationId == runGenerationId,
                "Responses-path tool calls must carry the run's GenerationId, not the opaque provider response id"
            );
    }

    [Fact]
    public async Task Falls_back_to_synthetic_generation_id_when_options_have_none()
    {
        // When the run advertises no GenerationId, the provider's own id remains (no behavior change).
        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(FunctionCallStreamWithResponseId("resp_X")));
        var stream = await agent.GenerateReplyStreamingAsync([new TextMessage { Role = Role.User, Text = "go" }]);

        var toolCalls = new List<ToolsCallMessage>();
        await foreach (var message in stream)
        {
            if (message is ToolsCallMessage tc)
            {
                toolCalls.Add(tc);
            }
        }

        toolCalls.Should().NotBeEmpty();
        toolCalls.Should().OnlyContain(tc => !string.IsNullOrEmpty(tc.GenerationId));
    }
}
