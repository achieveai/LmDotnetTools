using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     The middleware interface. For streaming-version middleware, check <see cref="IStreamingMiddleware" />.
/// </summary>
public interface IMiddleware
{
    /// <summary>
    ///     the name of the middleware
    /// </summary>
    string? Name { get; }

    /// <summary>
    ///     The method to invoke the middleware
    /// </summary>
    Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    );
}
