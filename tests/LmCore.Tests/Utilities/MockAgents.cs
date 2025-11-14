using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

/// <summary>
/// A mock implementation of IAgent that returns predefined responses for testing.
/// </summary>
public class MockAgent : IAgent
{
    private readonly IMessage _response;

    public MockAgent(IMessage response)
    {
        _response = response;
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult<IEnumerable<IMessage>>(new[] { _response });
    }
}

/// <summary>
/// A mock implementation of IStreamingAgent that returns predefined streaming responses for testing.
/// </summary>
public class MockStreamingAgent : IStreamingAgent
{
    private readonly IEnumerable<IMessage> _responseStream;

    public MockStreamingAgent(IEnumerable<IMessage> responseStream)
    {
        _responseStream = responseStream;
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // For non-streaming, just return the stream as a collection
        return Task.FromResult(
            _responseStream.Any() ? _responseStream : new[] { new TextMessage { Text = string.Empty } }
        );
    }

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(ConvertToAsyncEnumerable(_responseStream, cancellationToken));
    }

    private static async IAsyncEnumerable<IMessage> ConvertToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Optional: Add a small delay to simulate real streaming behavior
            await Task.Delay(5, cancellationToken);
            yield return message;
        }
    }
}

/// <summary>
/// A specialized mock streaming agent that simulates tool call updates.
/// </summary>
public class ToolCallStreamingAgent : IStreamingAgent
{
    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // For non-streaming just return a complete tool call
        var finalToolCall = CreateFinalToolCall();
        return Task.FromResult<IEnumerable<IMessage>>(new[] { finalToolCall });
    }

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(GenerateToolCallUpdatesAsync(cancellationToken));
    }

    private static async IAsyncEnumerable<IMessage> GenerateToolCallUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Simulate a sequence of tool call updates
        var updates = CreateToolCallUpdateSequence();

        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Add a small delay to simulate real streaming behavior
            await Task.Delay(5, cancellationToken);
            yield return update;
        }
    }

    private static ToolsCallMessage CreateFinalToolCall()
    {
        // Create a fully formed tool call
        var jsonArgs = System.Text.Json.JsonSerializer.Serialize(new { location = "San Francisco", unit = "celsius" });

        return new ToolsCallMessage
        {
            ToolCalls = System.Collections.Immutable.ImmutableList.Create(
                new ToolCall("get_weather", jsonArgs) { ToolCallId = "tool-123" }
            ),
        };
    }

    private static List<IMessage> CreateToolCallUpdateSequence()
    {
        return new List<IMessage>
        {
            // First update: Just the function name
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = System.Collections.Immutable.ImmutableList.Create(
                    new ToolCallUpdate { FunctionName = "get_weather" }
                ),
            },
            // Second update: With partial args
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = System.Collections.Immutable.ImmutableList.Create(
                    new ToolCallUpdate { FunctionName = "get_weather", FunctionArgs = "{\"location\":\"San" }
                ),
            },
            // Third update: More complete args
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = System.Collections.Immutable.ImmutableList.Create(
                    new ToolCallUpdate
                    {
                        FunctionName = "get_weather",
                        FunctionArgs = "{\"location\":\"San Francisco\"",
                    }
                ),
            },
            // Final update: Complete args
            new ToolsCallUpdateMessage
            {
                ToolCallUpdates = System.Collections.Immutable.ImmutableList.Create(
                    new ToolCallUpdate
                    {
                        FunctionName = "get_weather",
                        FunctionArgs = "{\"location\":\"San Francisco\",\"unit\":\"celsius\"}",
                    }
                ),
            },
        };
    }
}

/// <summary>
/// A specialized mock streaming agent that simulates text updates.
/// </summary>
public class TextStreamingAgent : IStreamingAgent
{
    private readonly string _fullText;

    public TextStreamingAgent(string fullText = "This is a sample streaming text message.")
    {
        _fullText = fullText;
    }

    public Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        // For non-streaming just return the full text
        return Task.FromResult<IEnumerable<IMessage>>(new[] { new TextMessage { Text = _fullText } });
    }

    public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(GenerateTextUpdatesAsync(cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> GenerateTextUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        // Break the full text into word chunks (keeping spaces with the following word)
        List<string> parts = new();
        string[] words = _fullText.Split(' ');

        // First word has no space prefix
        parts.Add(words[0]);

        // Remaining words have space prefixes
        for (int i = 1; i < words.Length; i++)
        {
            parts.Add(" " + words[i]);
        }

        // Stream the updates
        string accumulated = "";
        foreach (var part in parts)
        {
            accumulated += part;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken);
            yield return new AchieveAi.LmDotnetTools.LmCore.Messages.TextUpdateMessage { Text = accumulated };
        }
    }
}
