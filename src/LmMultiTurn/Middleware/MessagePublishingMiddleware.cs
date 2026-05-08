using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Middleware;

/// <summary>
/// Middleware that intercepts messages and publishes them to subscribers as a side effect.
/// Follows the interceptor pattern - yields all messages through unchanged while publishing.
/// Positioned BEFORE MessageUpdateJoinerMiddleware to capture streaming updates.
/// </summary>
internal sealed class MessagePublishingMiddleware : IStreamingMiddleware
{
    private readonly Func<IMessage, CancellationToken, ValueTask> _publishAction;

    public string? Name => "MessagePublishing";

    public MessagePublishingMiddleware(Func<IMessage, CancellationToken, ValueTask> publishAction)
    {
        ArgumentNullException.ThrowIfNull(publishAction);
        _publishAction = publishAction;
    }

    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken ct = default)
    {
        var messages = await agent.GenerateReplyAsync(context.Messages, context.Options, ct);
        var result = new List<IMessage>();

        foreach (var msg in messages)
        {
            await _publishAction(msg, ct);
            result.Add(msg);
        }

        return result;
    }

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken ct = default)
    {
        var stream = await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, ct);
        return ProcessAndPublishAsync(stream, ct);
    }

    private async IAsyncEnumerable<IMessage> ProcessAndPublishAsync(
        IAsyncEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var msg in messages.WithCancellation(ct))
        {
            // Publish as side effect (fire-and-forget for non-blocking)
            await _publishAction(msg, ct);

            // Always yield through unchanged
            yield return msg;
        }
    }
}
