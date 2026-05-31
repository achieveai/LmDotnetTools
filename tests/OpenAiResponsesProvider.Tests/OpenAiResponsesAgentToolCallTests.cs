using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Targeted tests for <see cref="OpenAiResponsesAgent"/>'s function-call emission, driven by a
///     hand-scripted event stream so individual event fields (notably the per-event
///     <c>item_id</c>) can be controlled precisely.
/// </summary>
public sealed class OpenAiResponsesAgentToolCallTests
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

    private static async Task<List<ToolsCallMessage>> RunAsync(IReadOnlyList<ResponseEvent> events)
    {
        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(events));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }]
        );

        var calls = new List<ToolsCallMessage>();
        await foreach (var message in stream)
        {
            if (message is ToolsCallMessage tc)
            {
                calls.Add(tc);
            }
        }

        return calls;
    }

    [Fact]
    public async Task Tool_call_emitted_from_output_item_done_when_delta_item_ids_rotate()
    {
        // The GitHub Copilot stream rotates (encrypts) the per-event item_id, so the
        // delta-correlation path can never match. The terminal output_item.done carries the
        // complete function_call — exactly one ToolsCallMessage must result (not zero, not two).
        var events = new ResponseEvent[]
        {
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCreated, Response = El("{\"id\":\"resp_1\"}") },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-A\",\"call_id\":\"call_1\",\"name\":\"add\"}"),
            },
            new ResponseFunctionCallArgumentsDeltaEvent { Type = ResponseEventTypes.FunctionCallArgumentsDelta, ItemId = "rotated-1", Delta = "{\"a\":1" },
            new ResponseFunctionCallArgumentsDoneEvent { Type = ResponseEventTypes.FunctionCallArgumentsDone, ItemId = "rotated-2", Arguments = "{\"a\":1,\"b\":2}" },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-Z\",\"call_id\":\"call_1\",\"name\":\"add\",\"arguments\":\"{\\\"a\\\":1,\\\"b\\\":2}\"}"),
            },
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCompleted, Response = El("{\"id\":\"resp_1\"}") },
        };

        var calls = await RunAsync(events);

        calls.Should().ContainSingle("rotated delta item_ids must yield exactly one tool call via output_item.done");
        var call = calls[0].ToolCalls.Should().ContainSingle().Subject;
        call.FunctionName.Should().Be("add");
        call.ToolCallId.Should().Be("call_1");
        call.FunctionArgs.Should().Contain("\"b\":2");
    }

    [Fact]
    public async Task Tool_call_not_duplicated_when_delta_path_and_output_item_done_both_resolve()
    {
        // Standard OpenAI stream: item_ids match, so the delta path emits first. The subsequent
        // output_item.done for the same call_id must be deduped (one tool call, not two).
        var events = new ResponseEvent[]
        {
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemAdded,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_9\",\"name\":\"mul\"}"),
            },
            new ResponseFunctionCallArgumentsDoneEvent { Type = ResponseEventTypes.FunctionCallArgumentsDone, ItemId = "item-1", Arguments = "{\"x\":3}" },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = El("{\"type\":\"function_call\",\"id\":\"item-1\",\"call_id\":\"call_9\",\"name\":\"mul\",\"arguments\":\"{\\\"x\\\":3}\"}"),
            },
        };

        var calls = await RunAsync(events);

        calls.Should().ContainSingle("the delta path and output_item.done must not both emit the same call_id");
        calls[0].ToolCalls.Should().ContainSingle().Which.ToolCallId.Should().Be("call_9");
    }
}
