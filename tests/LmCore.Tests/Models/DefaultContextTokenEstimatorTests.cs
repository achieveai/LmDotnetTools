using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

/// <summary>
///     The pre-send size heuristic (#681; spec 679 §4.2): text length / 4 + a fixed per-message overhead +
///     tool arguments and results / 4. Deterministic and explicitly an estimate.
/// </summary>
public class DefaultContextTokenEstimatorTests
{
    private static readonly DefaultContextTokenEstimator Estimator = DefaultContextTokenEstimator.Instance;

    [Fact]
    public void EmptyRequest_IsZero()
    {
        Estimator.Estimate([]).Should().Be(0);
    }

    [Fact]
    public void TextMessage_IsCharsOverFour_PlusOverhead()
    {
        var text = new string('x', 400);

        Estimator
            .Estimate([new TextMessage { Text = text, Role = Role.User }])
            .Should()
            .Be(100 + DefaultContextTokenEstimator.PerMessageOverheadTokens);
    }

    [Fact]
    public void PartialChunk_RoundsUp()
    {
        Estimator
            .Estimate([new TextMessage { Text = "abcde", Role = Role.User }])
            .Should()
            .Be(2 + DefaultContextTokenEstimator.PerMessageOverheadTokens);
    }

    [Fact]
    public void ToolCallsAndResults_CountArgumentsAndResultText()
    {
        var call = new ToolsCallMessage
        {
            Role = Role.Assistant,
            ToolCalls =
            [
                new ToolCall
                {
                    ToolCallId = "c1",
                    FunctionName = "read",
                    FunctionArgs = new string('a', 40),
                },
            ],
        };
        var result = new ToolsCallResultMessage
        {
            Role = Role.User,
            ToolCallResults = [new ToolCallResult("c1", new string('r', 80))],
        };

        Estimator
            .Estimate([call, result])
            .Should()
            .Be(
                (("read".Length + 40 + 3) / 4) + (80 / 4) + (2 * DefaultContextTokenEstimator.PerMessageOverheadTokens)
            );
    }

    [Fact]
    public void CompositeMessage_CountsItsPartsOnce()
    {
        var composite = new CompositeMessage
        {
            Role = Role.User,
            Messages = ImmutableList.Create<IMessage>(
                new TextMessage { Text = new string('a', 40), Role = Role.User },
                new TextMessage { Text = new string('b', 40), Role = Role.User }
            ),
        };

        Estimator.Estimate([composite]).Should().Be(20 + (2 * DefaultContextTokenEstimator.PerMessageOverheadTokens));
    }

    [Fact]
    public void ReasoningMessage_CountsItsText()
    {
        Estimator
            .Estimate([new ReasoningMessage { Reasoning = new string('t', 80), Role = Role.Assistant }])
            .Should()
            .Be(20 + DefaultContextTokenEstimator.PerMessageOverheadTokens);
    }

    [Fact]
    public void ImageMessage_UsesTheFixedImageBudget()
    {
        var image = new ImageMessage { ImageData = BinaryData.FromBytes(new byte[16]), Role = Role.User };

        Estimator
            .Estimate([image])
            .Should()
            .Be(DefaultContextTokenEstimator.ImageTokens + DefaultContextTokenEstimator.PerMessageOverheadTokens);
    }
}
