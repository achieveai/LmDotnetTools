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
                    new ToolCall { FunctionName = "lookup", FunctionArgs = "{}", ToolCallId = "call-1" },
                    new ToolCall { FunctionName = "fetch", FunctionArgs = "{}", ToolCallId = "call-2" },
                ],
                Role = Role.Assistant,
            },
        };

        var request = MessageMapper.BuildRequest(messages, options: null);

        request.Input.Should().HaveCount(2);
        request.Input.Select(i => i.Type).Should().AllBe("function_call");
        request.Input.Select(i => i.CallId).Should().Equal("call-1", "call-2");
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
        tool.Parameters.Should().NotBeNull();
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
