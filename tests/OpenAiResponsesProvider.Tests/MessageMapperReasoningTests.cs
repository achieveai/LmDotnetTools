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
}
