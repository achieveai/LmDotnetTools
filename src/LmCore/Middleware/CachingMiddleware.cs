#nullable enable

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

public class CachingMiddleware : IStreamingMiddleware
{
    /// <summary>
    /// Static JsonSerializerOptions used for serialization and deserialization of messages
    /// </summary>
    public static readonly JsonSerializerOptions S_jsonSerializerOptions = CreateJsonSerializerOptions();

    /// <summary>
    /// Creates a JsonSerializerOptions instance with all necessary converters for message serialization
    /// </summary>
    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        // Add the IMessage converter for polymorphic serialization
        options.Converters.Add(new IMessageJsonConverter());

        // Add converters for specific message types
        options.Converters.Add(new TextMessageJsonConverter());
        options.Converters.Add(new ImageMessageJsonConverter());
        options.Converters.Add(new ToolsCallMessageJsonConverter());
        options.Converters.Add(new ToolsCallResultMessageJsonConverter());
        options.Converters.Add(new ToolsCallAggregateMessageJsonConverter());
        options.Converters.Add(new TextUpdateMessageJsonConverter());
        options.Converters.Add(new ToolsCallUpdateMessageJsonConverter());

        return options;
    }

    private readonly IKvStore _kvStore;

    /// <summary>
    /// Creates a new caching middleware with the specified key-value store
    /// </summary>
    /// <param name="kvStore">Key-value store implementation to use for caching</param>
    public CachingMiddleware(IKvStore kvStore)
    {
        _kvStore = kvStore ?? throw new ArgumentNullException(nameof(kvStore));
    }

    public string? Name => "CachingMiddleware";

    /// <inheritdoc/>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
      MiddlewareContext context,
      IStreamingAgent agent,
      CancellationToken cancellationToken = default)
    {
        // Return the streaming messages implementation directly
        return await Task.FromResult(StreamMessages(context, agent, cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> StreamMessages(
        MiddlewareContext context,
        IStreamingAgent agent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = GetKey(
            context.Messages,
            context.Options);

        var values = await _kvStore.GetAsync<string[]>(key, cancellationToken);
        if (values != null)
        {
            foreach (var value in values)
            {
                var message = JsonSerializer.Deserialize<IMessage>(
                    value,
                    S_jsonSerializerOptions);

                if (message == null)
                {
                    continue;
                }

                await Task.Delay(20, cancellationToken);
                yield return message;
            }

            yield break;
        }

        var responses = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            context.Options,
            cancellationToken);

        var serializedMessages = new List<string>();
        await foreach (var response in responses)
        {
            if (response is IMessage message)
            {
                var serializedMessage = JsonSerializer.Serialize(
                    message,
                    S_jsonSerializerOptions);

                serializedMessages.Add(serializedMessage);
            }

            yield return response;
        }

        await _kvStore.SetAsync(key, serializedMessages);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
      MiddlewareContext context,
      IAgent agent,
      CancellationToken cancellationToken = default)
    {
        var key = GetKey(
            context.Messages,
            context.Options);

        var value = await _kvStore.GetAsync<string[]>(key, cancellationToken);
        if (value != null)
        {
            var message = JsonSerializer.Deserialize<IMessage>(
                value[0],
                S_jsonSerializerOptions);

            if (message == null)
            {
                return new List<IMessage>();
            }

            // In a more complete implementation, we might mark the message as coming from cache
            // For now, we'll just use it as-is

            var cachedResult = new List<IMessage>();
            cachedResult.Add(message);
            return cachedResult;
        }

        var responses = await agent.GenerateReplyAsync(
            context.Messages,
            context.Options,
            cancellationToken);

        var responseList = new List<IMessage>();
        var firstResponse = responses.FirstOrDefault();

        if (firstResponse != null)
        {
            var serializedMessage = JsonSerializer.Serialize(
                firstResponse,
                S_jsonSerializerOptions);

            await _kvStore.SetAsync(key, new[] { serializedMessage }, cancellationToken);
        }

        // Add all responses to our result list
        foreach (var response in responses)
        {
            responseList.Add(response);
        }

        return responseList;
    }

    public string GetKey(
      IEnumerable<IMessage> messages,
      GenerateReplyOptions? options)
    {
        var rawData = JsonSerializer.Serialize(
            new { messages, options },
            S_jsonSerializerOptions);

        // Create a SHA256
        using (SHA256 sha256Hash = SHA256.Create())
        {
            // ComputeHash - returns byte array
            byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
            return Convert.ToBase64String(bytes);
        }
    }
}