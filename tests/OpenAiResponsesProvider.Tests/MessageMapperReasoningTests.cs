using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Reasoning request plumbing. Reasoning-capable models (e.g. GPT-5.5 via Copilot) only return
///     reasoning summaries when the request asks for them. The request never set
///     <see cref="ResponseCreateRequest.Reasoning"/>, so no thinking blocks ever came back. Mirroring
///     the Anthropic "Thinking" convention, a <see cref="ResponseReasoningOptions"/> placed in
///     <see cref="GenerateReplyOptions.ExtraProperties"/> under key "Reasoning" must map onto the request.
/// </summary>
public sealed class MessageMapperReasoningTests
{
    private static readonly IMessage[] s_userTurn = [new TextMessage { Role = Role.User, Text = "hi" }];

    [Fact]
    public void Reasoning_option_from_extra_properties_maps_onto_request()
    {
        var options = new GenerateReplyOptions
        {
            ExtraProperties = ImmutableDictionary<string, object?>.Empty.Add(
                "Reasoning",
                new ResponseReasoningOptions { Effort = "high", Summary = "auto" }
            ),
        };

        var request = MessageMapper.BuildRequest(s_userTurn, options);

        request.Reasoning.Should().NotBeNull();
        request.Reasoning!.Effort.Should().Be("high");
        request.Reasoning.Summary.Should().Be("auto");
    }

    [Fact]
    public void No_reasoning_option_leaves_request_reasoning_null()
    {
        var request = MessageMapper.BuildRequest(s_userTurn, options: null);

        request.Reasoning.Should().BeNull();
    }

    private const string Model = "gpt-5.6-terra";
    private const string Origin = "responses:" + Model;

    private static GenerateReplyOptions ReplayOptions(bool reasoning = true) =>
        new()
        {
            ModelId = Model,
            ExtraProperties = reasoning
                ? ImmutableDictionary<string, object?>.Empty.Add(
                    "Reasoning",
                    new ResponseReasoningOptions { Summary = "auto" }
                )
                : ImmutableDictionary<string, object?>.Empty,
        };

    private static ReasoningMessage Blob(string? origin = Origin) =>
        new()
        {
            Role = Role.Assistant,
            Reasoning = "BLOB",
            Visibility = ReasoningVisibility.Encrypted,
            EncryptedOrigin = origin,
        };

    private static ToolsCallMessage Call() =>
        new()
        {
            Role = Role.Assistant,
            ToolCalls =
            [
                new ToolCall
                {
                    ToolCallId = "c1",
                    FunctionName = "calc",
                    FunctionArgs = "{}",
                },
            ],
        };

    private static TextMessage User(string text = "go") => new() { Role = Role.User, Text = text };

    private static IEnumerable<ResponseInputItem> ReasoningItems(ResponseCreateRequest request) =>
        request.Input.Where(i => i.Type == "reasoning");

    [Fact]
    public void Encrypted_reasoning_maps_to_input_item_before_its_function_call()
    {
        IMessage[] history =
        [
            User(),
            new ReasoningMessage
            {
                Role = Role.Assistant,
                Reasoning = "summary text",
                Visibility = ReasoningVisibility.Summary,
            },
            Blob(),
            Call(),
            new ToolsCallResultMessage { ToolCallResults = [new ToolCallResult("c1", "42")] },
        ];

        var request = MessageMapper.BuildRequest(history, ReplayOptions(), replayReasoning: true);

        request
            .Input.Select(i => i.Type)
            .Should()
            .Equal("message", "reasoning", "function_call", "function_call_output");
        System
            .Text.Json.JsonSerializer.Serialize(request.Input[1])
            .Should()
            .Be("{\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"BLOB\"}");
    }

    [Fact]
    public void Request_includes_encrypted_when_replay_on()
    {
        var request = MessageMapper.BuildRequest([User()], ReplayOptions(), replayReasoning: true);

        request.Include.Should().Equal("reasoning.encrypted_content");
        request.Store.Should().BeFalse("the blob is only returned for a response the server does not keep");
    }

    [Fact]
    public void Replay_on_without_reasoning_asks_for_no_blob()
    {
        var request = MessageMapper.BuildRequest([User()], ReplayOptions(reasoning: false), replayReasoning: true);

        request.Include.Should().BeNull();
        request.Store.Should().BeNull();
    }

    [Fact]
    public void Replay_off_leaves_request_unchanged()
    {
        var request = MessageMapper.BuildRequest([User()], ReplayOptions(), replayReasoning: false);

        request.Include.Should().BeNull();
        request.Store.Should().BeNull();
    }

    [Fact]
    public void Stored_rows_are_not_replayed_after_switch_off()
    {
        var request = MessageMapper.BuildRequest([User(), Blob(), Call(), User("next")], ReplayOptions());

        ReasoningItems(request).Should().BeEmpty("turning the switch off stops all replay at once");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("responses:gpt-5.5")]
    [InlineData("responses:gpt-5.6-terra-mini")]
    [InlineData("anthropic:gpt-5.6-terra")]
    public void Row_from_anywhere_else_is_not_replayed(string? origin)
    {
        var request = MessageMapper.BuildRequest(
            [User(), Blob(origin), Call(), User("next")],
            ReplayOptions(),
            replayReasoning: true
        );

        ReasoningItems(request).Should().BeEmpty("only the model that made a blob can read it");
    }

    [Theory]
    [InlineData("nothing", false)]
    [InlineData("user", false)]
    [InlineData("assistant", true)]
    public void Reasoning_is_kept_only_when_model_output_follows_it(string following, bool kept)
    {
        List<IMessage> history = [User(), Blob()];
        if (following == "user")
        {
            history.Add(User("next"));
        }
        else if (following == "assistant")
        {
            history.Add(new TextMessage { Role = Role.Assistant, Text = "done" });
        }

        var request = MessageMapper.BuildRequest(history, ReplayOptions(), replayReasoning: true);

        ReasoningItems(request).Should().HaveCount(kept ? 1 : 0);
    }
}
