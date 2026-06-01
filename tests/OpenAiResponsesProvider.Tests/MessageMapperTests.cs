using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;

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
                    new ToolCall { FunctionName = "lookup", FunctionArgs = "{\"q\":\"x\"}", ToolCallId = "call-1" },
                    new ToolCall { FunctionName = "fetch", FunctionArgs = "", ToolCallId = "call-2" },
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
        var messages = new IMessage[]
        {
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

        request.Input.Should().HaveCount(2);
        request.Input.Select(i => i.Type).Should().AllBe("function_call_output");
        request.Input.Select(i => i.CallId).Should().Equal("call-1", "call-2");
        request.Input.Select(i => i.Output).Should().Equal("result-A", "result-B");
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
            new ToolCallResultMessage { ToolCallId = "call_abc", Result = "42", Role = Role.Tool },
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
    public void CompositeMessage_wrapping_aggregate_tool_call_round_trips()
    {
        // This is the ACTUAL shape the multi-turn loop replays: an assistant turn grouped into a
        // CompositeMessage that contains a ToolsCallAggregateMessage (tool call + its result).
        // The mapper must unwrap both, or the model never sees the result and loops forever.
        var toolCall = new ToolsCallMessage
        {
            ToolCalls = [new ToolCall { FunctionName = "calculate", FunctionArgs = "{\"a\":17,\"b\":25}", ToolCallId = "call_xyz" }],
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

        var messages = new IMessage[] { new TextMessage { Role = Role.User, Text = "add 17 and 25" }, composite };

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
}
