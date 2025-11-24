using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that implements model fallback functionality.
/// It will attempt to use agents based on a model name mapping,
/// with fallback to other agents in the array if the first agent fails.
/// </summary>
public class ModelFallbackMiddleware : IStreamingMiddleware
{
    private readonly Dictionary<string, IAgent[]> _modelAgentMap;
    private readonly IAgent _defaultAgent;
    private readonly bool _tryDefaultLast;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelFallbackMiddleware"/> class.
    /// </summary>
    /// <param name="modelAgentMap">A dictionary mapping model names to arrays of agents to try in order.</param>
    /// <param name="defaultAgent">The default agent to use if no mapping is found for the model.</param>
    /// <param name="tryDefaultLast">If true, the default agent will be tried as a last resort after all mapped agents fail.</param>
    /// <param name="name">Optional name for the middleware.</param>
    public ModelFallbackMiddleware(
        Dictionary<string, IAgent[]> modelAgentMap,
        IAgent defaultAgent,
        bool tryDefaultLast = true,
        string? name = null
    )
    {
        _modelAgentMap = modelAgentMap ?? throw new ArgumentNullException(nameof(modelAgentMap));
        _defaultAgent = defaultAgent ?? throw new ArgumentNullException(nameof(defaultAgent));
        _tryDefaultLast = tryDefaultLast;
        Name = name ?? nameof(ModelFallbackMiddleware);
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware, attempting agents based on the model name in the options.
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        // Check if options is null or doesn't contain ModelId
        if (context.Options?.ModelId == null)
        {
            return await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
        }

        // Get the model name from options
        var modelId = context.Options.ModelId;

        // Try to get agents for the model
        if (_modelAgentMap.TryGetValue(modelId, out var agents) && agents.Length > 0)
        {
            Exception? lastException = null;

            // Try each agent in order
            foreach (var mappedAgent in agents)
            {
                try
                {
                    return await mappedAgent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Store the exception and try the next agent
                    lastException = ex;
                }
            }

            // If we should try the default agent as a last resort
            if (_tryDefaultLast)
            {
                try
                {
                    return await _defaultAgent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
                }
                catch (Exception) when (lastException != null)
                {
                    // If both mapped agents and default agent failed, throw the original exception
                    throw lastException;
                }
            }

            // If all agents failed and we're not trying default last, throw the last exception
            if (lastException != null)
            {
                throw lastException;
            }
        }

        // If no mapping was found for the model, use the default agent
        return await _defaultAgent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios, attempting agents based on the model name in the options.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        // Check if options is null or doesn't contain ModelId
        if (context.Options?.ModelId == null)
        {
            return await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
        }

        // Get the model name from options
        var modelId = context.Options.ModelId;

        // Try to get agents for the model
        if (_modelAgentMap.TryGetValue(modelId, out var agents) && agents.Length > 0)
        {
            Exception? lastException = null;

            // Try each agent in order
            foreach (var mappedAgent in agents)
            {
                try
                {
                    if (mappedAgent is IStreamingAgent streamingAgent)
                    {
                        return await streamingAgent.GenerateReplyStreamingAsync(
                            context.Messages,
                            context.Options,
                            cancellationToken
                        );
                    }
                    else
                    {
                        // If the agent doesn't support streaming, fall back to non-streaming
                        // and convert to an async enumerable
                        var result = await mappedAgent.GenerateReplyAsync(
                            context.Messages,
                            context.Options,
                            cancellationToken
                        );
                        return ToAsyncEnumerableInternal(result, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    // Store the exception and try the next agent
                    lastException = ex;
                }
            }

            // If we should try the default agent as a last resort
            if (_tryDefaultLast && _defaultAgent != null)
            {
                try
                {
                    if (_defaultAgent is IStreamingAgent streamingDefaultAgent)
                    {
                        return await streamingDefaultAgent.GenerateReplyStreamingAsync(
                            context.Messages,
                            context.Options,
                            cancellationToken
                        );
                    }
                    else
                    {
                        // If the agent doesn't support streaming, fall back to non-streaming
                        // and convert to an async enumerable
                        var result = await _defaultAgent.GenerateReplyAsync(
                            context.Messages,
                            context.Options,
                            cancellationToken
                        );
                        return ToAsyncEnumerableInternal(result, cancellationToken);
                    }
                }
                catch (Exception) when (lastException != null)
                {
                    // If both mapped agents and default agent failed, throw the original exception
                    throw lastException;
                }
            }

            // If all agents failed and we're not trying default last, throw the last exception
            if (lastException != null)
            {
                throw lastException;
            }
        }

        // If no mapping was found for the model, use the default agent
        if (_defaultAgent != null)
        {
            if (_defaultAgent is IStreamingAgent streamingDefaultAgent)
            {
                return await streamingDefaultAgent.GenerateReplyStreamingAsync(
                    context.Messages,
                    context.Options,
                    cancellationToken
                );
            }
            else
            {
                // If the agent doesn't support streaming, fall back to non-streaming
                // and convert to an async enumerable
                var result = await _defaultAgent.GenerateReplyAsync(
                    context.Messages,
                    context.Options,
                    cancellationToken
                );
                return ToAsyncEnumerableInternal(result, cancellationToken);
            }
        }

        // Fall back to using the provided agent if all else fails
        return await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    /// Helper method to convert IEnumerable to IAsyncEnumerable
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerableInternal(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield(); // Make this truly async
            yield return message;
        }
    }

    /// <summary>
    /// Helper class to create an IAsyncEnumerable from a single item.
    /// </summary>
    private class SingleItemAsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private readonly T _item;

        public SingleItemAsyncEnumerable(T item)
        {
            _item = item;
        }

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new SingleItemAsyncEnumerator<T>(_item);
        }
    }

    /// <summary>
    /// Helper class for a single item async enumerator.
    /// </summary>
    private class SingleItemAsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        private bool _moved;

        public SingleItemAsyncEnumerator(T item)
        {
            Current = item;
            _moved = false;
        }

        public T Current { get; }

        public ValueTask DisposeAsync()
        {
            return new ValueTask();
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (!_moved)
            {
                _moved = true;
                return new ValueTask<bool>(true);
            }

            return new ValueTask<bool>(false);
        }
    }
}
