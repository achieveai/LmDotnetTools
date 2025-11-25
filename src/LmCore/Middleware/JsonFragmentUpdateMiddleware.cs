using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
///     Middleware that processes ToolsCallUpdateMessage to add structured JSON fragment updates
///     based on the FunctionArgs using JsonFragmentToStructuredUpdateGenerator.
/// </summary>
public class JsonFragmentUpdateMiddleware : IStreamingMiddleware
{
    private readonly Dictionary<string, JsonFragmentToStructuredUpdateGenerator> _generators = [];

    public string? Name => "JsonFragmentUpdateMiddleware";

    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        var stream = await agent.GenerateReplyStreamingAsync(context.Messages, context.Options, cancellationToken);
        return ProcessAsync(stream);
    }

    public Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    ///     Processes messages and adds JsonFragmentUpdates to ToolsCallUpdateMessage instances.
    /// </summary>
    /// <param name="messageStream">The input stream of messages</param>
    /// <returns>The processed stream of messages with JsonFragmentUpdates added</returns>
    public async IAsyncEnumerable<IMessage> ProcessAsync(IAsyncEnumerable<IMessage> messageStream)
    {
        ArgumentNullException.ThrowIfNull(messageStream);
        await foreach (var message in messageStream)
        {
            yield return message is ToolsCallUpdateMessage toolsCallUpdateMessage
                ? ProcessToolsCallUpdateMessage(toolsCallUpdateMessage)
                : message;
        }
    }

    /// <summary>
    ///     Processes a ToolsCallUpdateMessage and adds JsonFragmentUpdates to each ToolCallUpdate.
    /// </summary>
    /// <param name="message">The ToolsCallUpdateMessage to process</param>
    /// <returns>A new ToolsCallUpdateMessage with JsonFragmentUpdates added</returns>
    private ToolsCallUpdateMessage ProcessToolsCallUpdateMessage(ToolsCallUpdateMessage message)
    {
        var updatedToolCallUpdates = new List<ToolCallUpdate>();

        foreach (var toolCallUpdate in message.ToolCallUpdates)
        {
            var updatedToolCallUpdate = ProcessToolCallUpdate(toolCallUpdate);
            updatedToolCallUpdates.Add(updatedToolCallUpdate);
        }

        return new ToolsCallUpdateMessage
        {
            FromAgent = message.FromAgent,
            Role = message.Role,
            Metadata = message.Metadata,
            GenerationId = message.GenerationId,
            ToolCallUpdates = [.. updatedToolCallUpdates],
        };
    }

    /// <summary>
    ///     Processes a single ToolCallUpdate and adds JsonFragmentUpdates based on its FunctionArgs.
    /// </summary>
    /// <param name="toolCallUpdate">The ToolCallUpdate to process</param>
    /// <returns>A new ToolCallUpdate with JsonFragmentUpdates added</returns>
    private ToolCallUpdate ProcessToolCallUpdate(ToolCallUpdate toolCallUpdate)
    {
        // If there are no function args, return the original update
        if (string.IsNullOrEmpty(toolCallUpdate.FunctionArgs))
        {
            return toolCallUpdate;
        }

        // Generate a key for the generator based on tool call identification
        var generatorKey = GetGeneratorKey(toolCallUpdate);

        // Get or create generator for this tool call
        if (!_generators.TryGetValue(generatorKey, out var generator))
        {
            generator = new JsonFragmentToStructuredUpdateGenerator(toolCallUpdate.FunctionName ?? "unknown");
            _generators[generatorKey] = generator;
        }

        // Process the function args fragment and get updates
        var jsonFragmentUpdates = generator.AddFragment(toolCallUpdate.FunctionArgs).ToImmutableList();

        // Return updated ToolCallUpdate with JsonFragmentUpdates
        return new ToolCallUpdate
        {
            ToolCallId = toolCallUpdate.ToolCallId,
            Index = toolCallUpdate.Index,
            FunctionName = toolCallUpdate.FunctionName,
            FunctionArgs = toolCallUpdate.FunctionArgs,
            JsonFragmentUpdates = jsonFragmentUpdates,
        };
    }

    /// <summary>
    ///     Generates a unique key for the generator based on tool call identification.
    /// </summary>
    /// <param name="toolCallUpdate">The ToolCallUpdate to generate a key for</param>
    /// <returns>A unique key for the generator</returns>
    private static string GetGeneratorKey(ToolCallUpdate toolCallUpdate)
    {
        // Prefer ToolCallId if available, otherwise use Index, otherwise use FunctionName
        return !string.IsNullOrEmpty(toolCallUpdate.ToolCallId) ? $"id:{toolCallUpdate.ToolCallId}"
            : toolCallUpdate.Index.HasValue ? $"index:{toolCallUpdate.Index.Value}"
            : $"name:{toolCallUpdate.FunctionName ?? "unknown"}";
    }

    /// <summary>
    ///     Clears all generators. Useful for resetting state between different tool call sequences.
    /// </summary>
    public void ClearGenerators()
    {
        _generators.Clear();
    }
}
