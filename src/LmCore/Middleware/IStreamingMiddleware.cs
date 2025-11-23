using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// The middleware interface. For streaming-version middleware, check <see cref="IStreamingMiddleware"/>.
/// </summary>
public interface IStreamingMiddleware : IMiddleware
{
    /// <summary>
    /// The method to invoke the middleware
    /// </summary>
    Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    );
}
