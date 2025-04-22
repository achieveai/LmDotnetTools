using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// A wrapper for IOpenClient that validates requests against stored test data.
/// Each instance handles a single request/response pair loaded from a file.
/// If the file doesn't exist, it will record the interaction with the inner client.
/// If the file exists but the request doesn't match, the test will fail.
/// </summary>
public class DatabasedClientWrapper : BaseClientWrapper, IOpenClient
{
    private readonly IOpenClient _innerClient;

    /// <summary>
    /// Creates a new instance of the DatabasedClientWrapper.
    /// </summary>
    /// <param name="innerClient">The inner OpenClient to wrap.</param>
    /// <param name="testDataFilePath">The path to the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    public DatabasedClientWrapper(IOpenClient innerClient, string testDataFilePath, bool allowAdditionalRequests = false)
        : base(testDataFilePath, allowAdditionalRequests)
    {
        _innerClient = innerClient;
    }

    /// <summary>
    /// Compare OpenAI-specific properties in request objects.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the OpenAI-specific properties match or are not present, false otherwise.</returns>
    protected static new bool CompareProviderSpecificProperties(JsonObject json1, JsonObject json2)
    {
        // Check OpenAI-specific properties
        if (!CompareCommonPropertyIfPresent(json1, json2, "frequency_penalty") ||
            !CompareCommonPropertyIfPresent(json1, json2, "presence_penalty") ||
            !CompareCommonPropertyIfPresent(json1, json2, "seed") ||
            !CompareCommonPropertyIfPresent(json1, json2, "stop"))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compare a common property if present in both objects.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <param name="propertyName">The property name to compare.</param>
    /// <returns>True if the properties match or are not present, false otherwise.</returns>
    private static bool CompareCommonPropertyIfPresent(JsonObject json1, JsonObject json2, string propertyName)
    {
        if (json1.ContainsKey(propertyName) && json2.ContainsKey(propertyName))
        {
            if (!CompareJsonValues(json1[propertyName], json2[propertyName]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Creates a chat completion by either returning cached data or recording a new interaction.
    /// </summary>
    /// <param name="chatCompletionRequest">The request to create a chat completion.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The chat completion response.</returns>
    public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
      ChatCompletionRequest chatCompletionRequest,
      CancellationToken cancellationToken = default)
    {
        // Serialize the request to JsonObject
        var serializedRequest = JsonSerializer.SerializeToNode(chatCompletionRequest, _jsonOptions)?.AsObject()
          ?? throw new InvalidOperationException("Failed to serialize request to JsonObject");

        // If we have test data, check if this request matches the next expected interaction
        if (_testData != null)
        {
            // Use our explicit tracking index to know which interaction to match
            int currentIndex = _currentInteractionIndex;

            // Check if we've exhausted all predefined interactions
            if (currentIndex >= _testData.Interactions.Count)
            {
                // If we allow additional requests, process this as a new interaction
                if (_allowAdditionalRequests)
                {
                    Console.WriteLine($"WARNING: Exceeded predefined interactions. Adding new non-streaming interaction at index {currentIndex}.");
                    return await ProcessNewNonStreamingInteraction(chatCompletionRequest, serializedRequest, cancellationToken);
                }

                // Otherwise throw an exception as before
                throw new InvalidOperationException($"Received more requests than expected. Expected {_testData.Interactions.Count} interactions, but received request #{currentIndex + 1}.");
            }

            var expectedInteraction = _testData.Interactions[currentIndex];

            // Skip streaming interactions if this is a non-streaming request
            if (expectedInteraction.IsStreaming)
            {
                throw new InvalidOperationException($"Expected a streaming request at index {currentIndex}, but received a non-streaming request.");
            }

            // Validate that the request matches
            if (!JsonObjectEquals(expectedInteraction.SerializedRequest, serializedRequest))
            {
                throw new InvalidOperationException($"The request at index {currentIndex} does not match the expected test data request.");
            }

            // Record this interaction and advance the index
            _recordedInteractions.Add(expectedInteraction);
            _currentInteractionIndex++;

            // Deserialize response JSON with our custom options
            // Let any deserialization errors propagate naturally
            var responseJson = expectedInteraction.SerializedResponse.ToJsonString();
            return JsonSerializer.Deserialize<ChatCompletionResponse>(responseJson, _jsonOptions)!;
        }

        return await ProcessNewNonStreamingInteraction(chatCompletionRequest, serializedRequest, cancellationToken);
    }

    /// <summary>
    /// Processes a new non-streaming interaction when no test data exists or when adding additional interactions.
    /// </summary>
    private async Task<ChatCompletionResponse> ProcessNewNonStreamingInteraction(
      ChatCompletionRequest chatCompletionRequest,
      JsonObject serializedRequest,
      CancellationToken cancellationToken)
    {
        // Call the inner client directly and let any exceptions propagate
        ChatCompletionResponse response = await _innerClient.CreateChatCompletionsAsync(
          chatCompletionRequest, cancellationToken);

        // Save the interaction to the file
        var serializedResponse = JsonSerializer.SerializeToNode(response, _jsonOptions)?.AsObject()
          ?? throw new InvalidOperationException("Failed to serialize response to JsonObject");

        // Create and store the interaction
        var interaction = new InteractionData
        {
            SerializedRequest = serializedRequest,
            SerializedResponse = serializedResponse,
            IsStreaming = false
        };

        _recordedInteractions.Add(interaction);
        _currentInteractionIndex++;

        // Save all recorded interactions to the file
        SaveTestData(_testDataFilePath, new TestData
        {
            Interactions = _recordedInteractions.ToList()
        });

        return response;
    }

    /// <summary>
    /// Creates a streaming chat completion by either returning cached data or recording a new interaction.
    /// </summary>
    /// <param name="chatCompletionRequest">The request to process.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of chat completion responses.</returns>
    public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
      ChatCompletionRequest chatCompletionRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Serialize the request to JsonObject
        var serializedRequest = JsonSerializer.SerializeToNode(chatCompletionRequest, _jsonOptions)?.AsObject()
          ?? throw new InvalidOperationException("Failed to serialize request to JsonObject");

        // If we have test data, check if this request matches the next expected interaction
        if (_testData != null)
        {
            // Use our explicit tracking index to know which interaction to match
            int currentIndex = _currentInteractionIndex;

            // Check if we've exhausted all predefined interactions
            if (currentIndex >= _testData.Interactions.Count)
            {
                // If we allow additional requests, process this as a new interaction
                if (_allowAdditionalRequests)
                {
                    Console.WriteLine($"WARNING: Exceeded predefined interactions. Adding new streaming interaction at index {currentIndex}.");
                    await foreach (var response in ProcessNewStreamingInteractionAsync(chatCompletionRequest, serializedRequest, cancellationToken))
                    {
                        yield return response;
                    }
                    yield break;
                }

                // Otherwise throw an exception as before
                throw new InvalidOperationException($"Received more requests than expected. Expected {_testData.Interactions.Count} interactions, but received request #{currentIndex + 1}.");
            }

            var expectedInteraction = _testData.Interactions[currentIndex];

            // Skip non-streaming interactions if this is a streaming request
            if (!expectedInteraction.IsStreaming)
            {
                throw new InvalidOperationException($"Expected a non-streaming request at index {currentIndex}, but received a streaming request.");
            }

            // Validate that the request matches
            if (!JsonObjectEquals(expectedInteraction.SerializedRequest, serializedRequest))
            {
                throw new InvalidOperationException($"The request at index {currentIndex} does not match the expected test data request.");
            }

            // Record this interaction and advance the index
            _recordedInteractions.Add(expectedInteraction);
            _currentInteractionIndex++;

            // Return each fragment with a small delay between them
            foreach (var fragmentJson in expectedInteraction.SerializedResponseFragments)
            {
                // Add a small delay between fragments to simulate streaming
                await Task.Delay(1, cancellationToken);
                yield return JsonSerializer.Deserialize<ChatCompletionResponse>(fragmentJson.ToJsonString(), _jsonOptions)!;
            }

            yield break;
        }

        await foreach (var response in ProcessNewStreamingInteractionAsync(chatCompletionRequest, serializedRequest, cancellationToken))
        {
            yield return response;
        }
    }

    /// <summary>
    /// Processes a new streaming interaction when no test data exists or when adding additional interactions.
    /// </summary>
    private async IAsyncEnumerable<ChatCompletionResponse> ProcessNewStreamingInteractionAsync(
      ChatCompletionRequest chatCompletionRequest,
      JsonObject serializedRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get responses from inner client or create mock response
        var responses = await GetStreamingResponses(
          chatCompletionRequest,
          serializedRequest,
          cancellationToken);

        // Return all responses
        foreach (var response in responses)
        {
            yield return response;
        }
    }

    /// <summary>
    /// Helper method to get streaming responses either from the inner client or create a mock response
    /// </summary>
    private async Task<List<ChatCompletionResponse>> GetStreamingResponses(
      ChatCompletionRequest chatCompletionRequest,
      JsonObject serializedRequest,
      CancellationToken cancellationToken)
    {
        List<JsonObject> responseFragments = new();
        List<ChatCompletionResponse> responses = new();

        await foreach (ChatCompletionResponse response in _innerClient.StreamingChatCompletionsAsync(
          chatCompletionRequest, cancellationToken))
        {
            // Save the fragment
            var serializedFragment = JsonSerializer.SerializeToNode(response, _jsonOptions)?.AsObject()
              ?? throw new InvalidOperationException("Failed to serialize response fragment to JsonObject");
            responseFragments.Add(serializedFragment);
            responses.Add(response);
        }

        // Create and store the interaction
        var interaction = new InteractionData
        {
            SerializedRequest = serializedRequest,
            SerializedResponseFragments = responseFragments,
            IsStreaming = true
        };

        _recordedInteractions.Add(interaction);
        _currentInteractionIndex++;

        // Save all recorded interactions to the file
        SaveTestData(_testDataFilePath, new TestData
        {
            Interactions = _recordedInteractions.ToList()
        });

        return responses;
    }

    /// <summary>
    /// Disposes of the inner client.
    /// </summary>
    public override void Dispose()
    {
        _innerClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Represents test data containing an ordered collection of request/response interactions.
/// </summary>
public record TestData
{
    /// <summary>
    /// Gets or sets the collection of interaction data.
    /// </summary>
    public List<InteractionData> Interactions { get; init; } = new();
}

/// <summary>
/// Represents a single request/response interaction.
/// </summary>
public record InteractionData
{
    /// <summary>
    /// Gets or sets the serialized request.
    /// </summary>
    public JsonObject SerializedRequest { get; init; } = new JsonObject();

    /// <summary>
    /// Gets or sets the serialized response for non-streaming requests.
    /// </summary>
    public JsonObject SerializedResponse { get; init; } = new JsonObject();

    /// <summary>
    /// Gets or sets the serialized response fragments for streaming requests.
    /// </summary>
    public List<JsonObject> SerializedResponseFragments { get; init; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this is a streaming interaction.
    /// </summary>
    public bool IsStreaming { get; init; }
}

/// <summary>
/// Legacy test data format for backward compatibility.
/// </summary>
public record LegacyTestData
{
    /// <summary>
    /// Gets or sets the serialized request.
    /// </summary>
    public JsonObject SerializedRequest { get; init; } = new JsonObject();

    /// <summary>
    /// Gets or sets the serialized response for non-streaming requests.
    /// </summary>
    public JsonObject SerializedResponse { get; init; } = new JsonObject();

    /// <summary>
    /// Gets or sets the serialized response fragments for streaming requests.
    /// </summary>
    public List<JsonObject> SerializedResponseFragments { get; init; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether this is a streaming interaction.
    /// </summary>
    public bool IsStreaming { get; init; }
}
