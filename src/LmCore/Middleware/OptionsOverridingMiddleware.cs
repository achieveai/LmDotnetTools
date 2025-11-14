using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that overrides options in the middleware context with values passed during construction.
/// </summary>
public class OptionsOverridingMiddleware : IStreamingMiddleware
{
    private readonly GenerateReplyOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptionsOverridingMiddleware"/> class.
    /// </summary>
    /// <param name="options">The options to override with.</param>
    /// <param name="name">Optional name for the middleware.</param>
    public OptionsOverridingMiddleware(GenerateReplyOptions options, string? name = null)
    {
        Name = name ?? nameof(OptionsOverridingMiddleware);
        _options = options;
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware, overriding options in the context and forwarding to the next agent.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Create a new context with overridden options
        var overriddenOptions = OverrideOptions(context.Options);
        var newContext = new MiddlewareContext(context.Messages, overriddenOptions);

        // Forward to the next agent
        return await agent.GenerateReplyAsync(newContext.Messages, overriddenOptions, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios, overriding options in the context and forwarding to the next agent.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Create a new context with overridden options
        var overriddenOptions = OverrideOptions(context.Options);
        var newContext = new MiddlewareContext(context.Messages, overriddenOptions);

        // Forward to the next agent
        return await agent.GenerateReplyStreamingAsync(newContext.Messages, overriddenOptions, cancellationToken);
    }

    private GenerateReplyOptions OverrideOptions(GenerateReplyOptions? currentOptions)
    {
        // If there are no current options, just use the override options
        if (currentOptions == null)
        {
            return _options;
        }

        // Use the new Merge method to merge the options
        // Current options are the base, and override options take precedence
        return currentOptions.Merge(_options);
    }
}
