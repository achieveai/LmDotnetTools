using System.Collections.Immutable;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Unit coverage for <see cref="MessageMapper"/>. The mapper is the sole translator between
///     the unified <see cref="IMessage"/> model and the Responses API wire format, so each
///     branch (system/user/assistant/tool-call/tool-result/options) is exercised explicitly —
///     a wrong <c>type</c> string or missing <c>call_id</c> would cause silent context drops.
/// </summary>
public sealed class MessageMapperTests
{
    [Fact]
    public void System_messages_concatenate_into_instructions_with_newline_join()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "first system" },
            new TextMessage { Role = Role.System, Text = "second system" },
            new TextMessage { Role = Role.User, Text = "hi" },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Instructions.Should().Be("first system\nsecond system");
        request.Input.Should().HaveCount(1);
        request.Input[0].Type.Should().Be("message");
        request.Input[0].Role.Should().Be("user");
    }

    [Theory]
    [InlineData(AgentMessageType.DelegateTask)]
    [InlineData(AgentMessageType.Response)]
    [InlineData(AgentMessageType.Question)]
    [InlineData(AgentMessageType.Steer)]
    public void Agent_message_keeps_payload_and_reply_routing(AgentMessageType messageType)
    {
        var message = AgentMessage.Create("message-1", messageType, "sender-id", "sender", "Please respond.");
        var request = MessageMapper.BuildRequest(
            [new TextMessage { Role = Role.Assistant, Text = "Done." }, message],
            null
        );
        request.Input.Should().HaveCount(2);
        var last = request.Input.Last();
        last.Role.Should().Be("user");
        last.Content.Should().ContainSingle();
        last.Content![0].Type.Should().Be("input_text");
        last.Content[0].Text.Should().Be(message.Text);
    }

    [Fact]
    public void User_text_emits_input_text_part()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "ping" },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(1);
        var item = request.Input[0];
        item.Type.Should().Be("message");
        item.Role.Should().Be("user");
        item.Content.Should().NotBeNull();
        item.Content![0].Type.Should().Be("input_text");
        item.Content[0].Text.Should().Be("ping");
    }

    [Fact]
    public void Assistant_text_emits_output_text_part()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.Assistant, Text = "pong" },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(1);
        var item = request.Input[0];
        item.Role.Should().Be("assistant");
        item.Content.Should().NotBeNull();
        item.Content![0].Type.Should().Be("output_text");
        item.Content[0].Text.Should().Be("pong");
    }

    [Fact]
    public void ToolsCallMessage_emits_function_call_items_with_call_id()
    {
        var messages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "lookup",
                        FunctionArgs = "{\"q\":\"x\"}",
                        ToolCallId = "call-1",
                    },
                    new ToolCall
                    {
                        FunctionName = "fetch",
                        FunctionArgs = "",
                        ToolCallId = "call-2",
                    },
                ],
                Role = Role.Assistant,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(2);
        request.Input.Select(i => i.Type).Should().AllBe("function_call");
        request.Input.Select(i => i.CallId).Should().Equal("call-1", "call-2");
        // A replayed function_call MUST carry name + arguments or the API 400s. Empty args default to "{}".
        request.Input.Select(i => i.Name).Should().Equal("lookup", "fetch");
        request.Input.Select(i => i.Arguments).Should().Equal("{\"q\":\"x\"}", "{}");
        request.Input.Should().OnlyContain(i => i.Content == null);
    }

    [Fact]
    public void ToolsCallResultMessage_emits_function_call_output_items()
    {
        // Both results are paired: an output with no matching call is dropped before it reaches the
        // wire, so an unpaired fixture here would assert the pairing sweep rather than the mapping.
        var messages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls = ImmutableList.Create(
                    new ToolCall
                    {
                        FunctionName = "f",
                        FunctionArgs = "{}",
                        ToolCallId = "call-1",
                    },
                    new ToolCall
                    {
                        FunctionName = "g",
                        FunctionArgs = "{}",
                        ToolCallId = "call-2",
                    }
                ),
                Role = Role.Assistant,
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = ImmutableList.Create(
                    new ToolCallResult("call-1", "result-A"),
                    new ToolCallResult("call-2", "result-B")
                ),
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        var outputs = request.Input.Where(i => i.Type == "function_call_output").ToList();
        outputs.Should().HaveCount(2);
        outputs.Select(i => i.CallId).Should().Equal("call-1", "call-2");
        outputs.Select(i => i.Output).Should().Equal("result-A", "result-B");
    }

    [Fact]
    public void Functions_in_options_emit_tools_array_with_serialized_parameters()
    {
        var fn = new FunctionContract
        {
            Name = "search",
            Description = "search the web",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "q",
                    Description = "query",
                    IsRequired = true,
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                },
            ],
        };
        var options = new GenerateReplyOptions { Functions = [fn] };

        var request = MessageMapper.BuildRequest([], options);

        request.Tools.Should().NotBeNull();
        request.Tools.Should().HaveCount(1);
        var tool = request.Tools![0];
        tool.Type.Should().Be("function");
        tool.Name.Should().Be("search");
        tool.Description.Should().Be("search the web");

        // parameters MUST be a JSON Schema object, not the raw parameter list (which serializes to an
        // array). The Responses API rejects an array with "expected an object, but got an array".
        tool.Parameters.Should().NotBeNull();
        tool.Parameters.Should().BeOfType<System.Text.Json.Nodes.JsonObject>();
        var parameters = tool.Parameters!.AsObject();
        parameters["type"]!.GetValue<string>().Should().Be("object");
        parameters["properties"].Should().NotBeNull();
        parameters["properties"]!.AsObject().Should().ContainKey("q");
        parameters["properties"]!["q"]!["description"]!.GetValue<string>().Should().Be("query");
        parameters["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("q");
    }

    [Fact]
    public void Singular_ToolCallMessage_and_result_round_trip_as_function_call_items()
    {
        // The multi-turn loop replays history as SINGULAR ToolCallMessage / ToolCallResultMessage.
        // These must map to function_call + function_call_output (with matching call_id) or the model
        // never sees the tool result and loops, re-calling the tool forever.
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "add 17 and 25" },
            new ToolCallMessage
            {
                FunctionName = "add",
                FunctionArgs = "{\"a\":17,\"b\":25}",
                ToolCallId = "call_abc",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call_abc",
                Result = "42",
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(3);
        request.Input[1].Type.Should().Be("function_call");
        request.Input[1].CallId.Should().Be("call_abc");
        request.Input[1].Name.Should().Be("add");
        request.Input[1].Arguments.Should().Be("{\"a\":17,\"b\":25}");
        request.Input[2].Type.Should().Be("function_call_output");
        request.Input[2].CallId.Should().Be("call_abc");
        request.Input[2].Output.Should().Be("42");
    }

    [Fact]
    public void Orphaned_function_call_output_is_dropped_instead_of_reaching_the_wire()
    {
        // A history edit upstream (a compaction cut, a projection that removes rows by seq) can hide
        // an assistant tool call while its result survives. The Responses API answers the whole
        // request with 400 "No tool call found for function call output with call_id ...", and since
        // the same history replays every turn the conversation is wedged permanently. Dropping the
        // widowed half degrades the request instead of failing it.
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "add 17 and 25" },
            new ToolCallResultMessage
            {
                ToolCallId = "call_orphan",
                Result = "42",
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().ContainSingle();
        request.Input[0].Type.Should().Be("message");
    }

    [Fact]
    public void Orphaned_output_is_dropped_while_paired_items_keep_their_order()
    {
        // One ToolsCallResultMessage can answer several calls, only some of which survived the cut.
        // The drop is per call_id, not per message, and everything kept stays in order.
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "go" },
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "add",
                        FunctionArgs = "{}",
                        ToolCallId = "call_kept",
                    },
                ],
                Role = Role.Assistant,
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call_gone", "orphan"), new ToolCallResult("call_kept", "42")],
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(3);
        request.Input[0].Type.Should().Be("message");
        request.Input[1].Type.Should().Be("function_call");
        request.Input[1].CallId.Should().Be("call_kept");
        request.Input[2].Type.Should().Be("function_call_output");
        request.Input[2].CallId.Should().Be("call_kept");
        request.Input.Select(i => i.CallId).Should().NotContain("call_gone");
    }

    [Fact]
    public void Unanswered_function_call_is_kept_because_the_api_accepts_it()
    {
        // The mirror image is LEGAL: a turn may end with a call the tool has not answered yet, and
        // the API accepts it. Dropping it would delete real history to fix a problem that is not one.
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "add 17 and 25" },
            new ToolCallMessage
            {
                FunctionName = "add",
                FunctionArgs = "{\"a\":17,\"b\":25}",
                ToolCallId = "call_pending",
                Role = Role.Assistant,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(2);
        request.Input[1].Type.Should().Be("function_call");
        request.Input[1].CallId.Should().Be("call_pending");
    }

    [Fact]
    public void Dropping_an_orphaned_output_is_logged_at_warning_with_the_call_id()
    {
        // The drop silently changes what the model sees, so it must leave a trace naming the id —
        // that id is the only handle on the upstream defect that widowed the output.
        var logger = new CapturingLogger();
        var messages = new IMessage[]
        {
            new ToolCallResultMessage
            {
                ToolCallId = "call_orphan",
                Result = "42",
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null, logger);

        request.Input.Should().BeEmpty();
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        logger.Entries[0].Message.Should().Contain("call_orphan");
    }

    [Fact]
    public void A_paired_request_logs_no_warning()
    {
        // Non-vacuity guard for the test above: the warning must be caused by the orphan, not by
        // every request that happens to carry a tool result.
        var logger = new CapturingLogger();
        var messages = new IMessage[]
        {
            new ToolCallMessage
            {
                FunctionName = "add",
                FunctionArgs = "{}",
                ToolCallId = "call_abc",
                Role = Role.Assistant,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call_abc",
                Result = "42",
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null, logger);

        request.Input.Should().HaveCount(2);
        logger.Entries.Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void CompositeMessage_wrapping_aggregate_tool_call_round_trips()
    {
        // This is the ACTUAL shape the multi-turn loop replays: an assistant turn grouped into a
        // CompositeMessage that contains a ToolsCallAggregateMessage (tool call + its result).
        // The mapper must unwrap both, or the model never sees the result and loops forever.
        var toolCall = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "calculate",
                    FunctionArgs = "{\"a\":17,\"b\":25}",
                    ToolCallId = "call_xyz",
                },
            ],
            Role = Role.Assistant,
        };
        var toolResult = new ToolsCallResultMessage
        {
            ToolCallResults = [new ToolCallResult("call_xyz", "42")],
            Role = Role.Tool,
        };
        var composite = new CompositeMessage
        {
            Messages = [new ToolsCallAggregateMessage(toolCall, toolResult)],
            Role = Role.Assistant,
        };

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "add 17 and 25" },
            composite,
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Select(i => i.Type).Should().Equal("message", "function_call", "function_call_output");
        var call = request.Input[1];
        call.CallId.Should().Be("call_xyz");
        call.Name.Should().Be("calculate");
        call.Arguments.Should().Be("{\"a\":17,\"b\":25}");
        var output = request.Input[2];
        output.CallId.Should().Be("call_xyz");
        output.Output.Should().Be("42");
    }

    [Fact]
    public void Sampling_options_passthrough()
    {
        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-test",
            Temperature = 0.7f,
            TopP = 0.9f,
            MaxToken = 256,
            ToolChoice = "auto",
        };

        var request = MessageMapper.BuildRequest([], options);

        request.Model.Should().Be("gpt-test");
        request.Temperature.Should().Be(0.7f);
        request.TopP.Should().Be(0.9f);
        request.MaxOutputTokens.Should().Be(256);
        request.ToolChoice.Should().NotBeNull();
        request.ToolChoice!.GetValue<string>().Should().Be("auto");
        request.Stream.Should().BeTrue();
    }

    [Fact]
    public void Empty_modelId_does_not_set_model_field()
    {
        var options = new GenerateReplyOptions { ModelId = string.Empty };

        var request = MessageMapper.BuildRequest([], options);

        request.Model.Should().BeNull();
    }

    [Fact]
    public void No_messages_no_options_produces_empty_input_and_no_instructions()
    {
        var request = MessageMapper.BuildRequest([], options: null);

        request.Input.Should().BeEmpty();
        request.Instructions.Should().BeNull();
        request.Tools.Should().BeNull();
        request.Stream.Should().BeTrue();
    }

    [Fact]
    public void Unknown_role_defaults_to_user_via_MapRole_fallback()
    {
        // Role values outside the known enum members should not crash — the mapper falls
        // back to "user" for forward-compat with new repo-level role types.
        var messages = new IMessage[]
        {
            new TextMessage { Role = (Role)99, Text = "unknown role" },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(1);
        request.Input[0].Role.Should().Be("user");
    }

    // #694 — history persisted before the LmCore bound existed can still carry a 15,231,668-byte
    // result. The Responses API rejects function_call_output.output above 10,485,760, so the
    // mapper clamps at that hard limit as a last line of defence.
    [Fact]
    public void Oversized_persisted_tool_results_are_clamped_to_the_responses_output_limit()
    {
        const int hardLimit = 10_485_760;
        var oversized = new string('q', 15_231_668);
        // Both results are paired with their calls: an unpaired output is dropped before the clamp
        // ever sees it, which would make this a test of the pairing sweep instead.
        var messages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "f",
                        FunctionArgs = "{}",
                        ToolCallId = "call-1",
                    },
                ],
                Role = Role.Assistant,
            },
            new ToolCallMessage
            {
                FunctionName = "f",
                FunctionArgs = "{}",
                ToolCallId = "call-2",
                Role = Role.Assistant,
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call-1", oversized)],
                Role = Role.Tool,
            },
            new ToolCallResultMessage
            {
                ToolCallId = "call-2",
                Result = oversized,
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        var outputs = request.Input.Where(i => i.Type == "function_call_output").ToList();
        outputs.Should().HaveCount(2);
        foreach (var item in outputs)
        {
            item.Type.Should().Be("function_call_output");
            item.Output.Should().NotBeNull();
            Encoding.UTF8.GetByteCount(item.Output!).Should().BeLessThanOrEqualTo(hardLimit);
            item.Output.Should().StartWith(oversized[..4096]);
            item.Output.Should().Contain(ToolResultLimits.TruncationMarkerPrefix);
            item.Output.Should().EndWith(" of 15,231,668 bytes]");
        }
    }

    [Fact]
    public void Tool_results_under_the_responses_output_limit_pass_through_untouched()
    {
        var messages = new IMessage[]
        {
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "f",
                        FunctionArgs = "{}",
                        ToolCallId = "call-1",
                    },
                ],
                Role = Role.Assistant,
            },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("call-1", "result-A")],
                Role = Role.Tool,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Single(i => i.Type == "function_call_output").Output.Should().Be("result-A");
    }
}
