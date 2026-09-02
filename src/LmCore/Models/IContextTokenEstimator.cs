using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     Estimates the input-token size of a request BEFORE it is sent (#681; spec 679 §4.2). Applied to the
///     execution view the loop is about to dispatch, not to raw history. A provider with a count endpoint
///     may implement this precisely; the default is a deterministic heuristic and says so through
///     <see cref="MeasurementProvenance.Estimated" />.
/// </summary>
public interface IContextTokenEstimator
{
    /// <summary>Estimated input tokens for <paramref name="request" /> as it will be sent.</summary>
    long Estimate(IReadOnlyList<IMessage> request);
}

/// <summary>
///     The default heuristic: per message, text characters / 4 (rounded up) plus a fixed overhead, with tool
///     names, arguments and results counted as text and an image counted at a fixed budget.
/// </summary>
/// <remarks>
///     Deliberately simple and provider-neutral: the number is a pressure signal, not a bill. The measured
///     count that follows the response (input + cache read + cache write of the generation's usage) replaces
///     it on the same observation, and the two are never confused because the observation carries its
///     <see cref="MeasurementProvenance" />.
/// </remarks>
public sealed class DefaultContextTokenEstimator : IContextTokenEstimator
{
    /// <summary>Characters per token assumed by the heuristic.</summary>
    public const int CharsPerToken = 4;

    /// <summary>Tokens charged per message for role and framing.</summary>
    public const int PerMessageOverheadTokens = 12;

    /// <summary>Tokens charged per image, whose real cost depends on dimensions the estimator cannot see.</summary>
    public const int ImageTokens = 1_000;

    /// <summary>The shared instance; the estimator holds no state.</summary>
    public static DefaultContextTokenEstimator Instance { get; } = new();

    /// <inheritdoc />
    public long Estimate(IReadOnlyList<IMessage> request)
    {
        ArgumentNullException.ThrowIfNull(request);

        long total = 0;
        foreach (var message in request)
        {
            total += EstimateMessage(message);
        }

        return total;
    }

    private static long EstimateMessage(IMessage message)
    {
        return message switch
        {
            CompositeMessage composite => composite.Messages.Sum(EstimateMessage),
            ToolsCallAggregateMessage aggregate => EstimateMessage(aggregate.ToolsCallMessage)
                + EstimateMessage(aggregate.ToolsCallResult),
            ImageMessage => ImageTokens + PerMessageOverheadTokens,
            _ => Tokens(Chars(message)) + PerMessageOverheadTokens,
        };
    }

    private static long Chars(IMessage message)
    {
        return message switch
        {
            TextMessage text => text.Text?.Length ?? 0,
            ReasoningMessage reasoning => reasoning.Reasoning?.Length ?? 0,
            ToolsCallMessage calls => calls.ToolCalls.Sum(c =>
                (c.FunctionName?.Length ?? 0) + (c.FunctionArgs?.Length ?? 0)
            ),
            ToolCallMessage call => (call.FunctionName?.Length ?? 0) + (call.FunctionArgs?.Length ?? 0),
            ToolsCallResultMessage results => results.ToolCallResults.Sum(r => r.Result?.Length ?? 0),
            ToolCallResultMessage result => result.Result?.Length ?? 0,
            _ => 0,
        };
    }

    private static long Tokens(long chars) => (chars + CharsPerToken - 1) / CharsPerToken;
}
