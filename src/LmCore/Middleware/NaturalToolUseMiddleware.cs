using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Middleware that combines NaturalToolUseParserMiddleware and FunctionCallMiddleware
/// to enable natural language tool use within LLM responses.
/// </summary>
public class NaturalToolUseMiddleware : IStreamingMiddleware
{
    private readonly NaturalToolUseParserMiddleware _parserMiddleware;
    private readonly FunctionCallMiddleware _functionCallMiddleware;

    /// <summary>
    /// Creates a new instance of NaturalToolUseMiddleware.
    /// </summary>
    /// <param name="functions">The function contracts to be used for tool calls.</param>
    /// <param name="functionMap">A dictionary mapping function names to their implementations.</param>
    /// <param name="fallbackParser">Optional agent to use for parsing invalid tool calls.</param>
    /// <param name="name">Optional name for the middleware.</param>
    /// <param name="schemaValidator">Optional schema validator for validating tool call arguments.</param>
    public NaturalToolUseMiddleware(
        IEnumerable<FunctionContract> functions,
        IDictionary<string, Func<string, Task<string>>> functionMap,
        IAgent? fallbackParser = null,
        string? name = null,
        IJsonSchemaValidator? schemaValidator = null
    )
    {
        ArgumentNullException.ThrowIfNull(functions);

        ArgumentNullException.ThrowIfNull(functionMap);

        Name = name ?? nameof(NaturalToolUseMiddleware);

        // Create the parser middleware
        _parserMiddleware = new NaturalToolUseParserMiddleware(
            functions,
            schemaValidator,
            fallbackParser,
            $"{Name}.Parser"
        );

        // Create the function call middleware
        _functionCallMiddleware = new FunctionCallMiddleware(functions, functionMap, $"{Name}.FunctionCall");
    }

    /// <summary>
    /// Gets the name of the middleware.
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// Invokes the middleware to process a request and generate a response.
    /// </summary>
    /// <param name="context">The middleware context containing messages and options.</param>
    /// <param name="agent">The agent to use for generating responses.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A collection of messages representing the response.</returns>
    public Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Use the extension method to chain middleware components
        return agent
            .WithMiddleware(_parserMiddleware)
            .WithMiddleware(_functionCallMiddleware)
            .GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware to process a request and generate a streaming response.
    /// </summary>
    /// <param name="context">The middleware context containing messages and options.</param>
    /// <param name="agent">The streaming agent to use for generating responses.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An asynchronous stream of messages representing the response.</returns>
    public Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        // Use the extension method to chain middleware components
        return agent
            .WithMiddleware(_parserMiddleware)
            .WithMiddleware(_functionCallMiddleware)
            .GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
    }
}
