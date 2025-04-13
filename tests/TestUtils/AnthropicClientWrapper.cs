using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// A wrapper for IAnthropicClient that validates requests against stored test data.
/// Each instance handles a single request/response pair loaded from a file.
/// If the file doesn't exist, it will record the interaction with the inner client.
/// If the file exists but the request doesn't match, the test will fail.
/// </summary>
public class AnthropicClientWrapper : BaseClientWrapper, IAnthropicClient
{
    private readonly IAnthropicClient _innerClient;

    /// <summary>
    /// Creates a new instance of the AnthropicClientWrapper.
    /// </summary>
    /// <param name="innerClient">The inner AnthropicClient to wrap.</param>
    /// <param name="testDataFilePath">The path to the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    public AnthropicClientWrapper(IAnthropicClient innerClient, string testDataFilePath, bool allowAdditionalRequests = false)
        : base(testDataFilePath, allowAdditionalRequests)
    {
        _innerClient = innerClient;
    }

    /// <summary>
    /// Compare Anthropic-specific properties in request objects.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the Anthropic-specific properties match or are not present, false otherwise.</returns>
    protected static new bool CompareProviderSpecificProperties(JsonObject json1, JsonObject json2)
    {
        // Check Anthropic-specific properties
        if (!CompareCommonPropertyIfPresent(json1, json2, "thinking") ||
            !CompareCommonPropertyIfPresent(json1, json2, "stream") ||
            !CompareCommonPropertyIfPresent(json1, json2, "tools") ||
            !CompareCommonPropertyIfPresent(json1, json2, "tool_choice"))
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
    /// <param name="request">The request to create a chat completion.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The chat completion response.</returns>
    public async Task<AnthropicResponse> CreateChatCompletionsAsync(
        AnthropicRequest request,
        CancellationToken cancellationToken = default)
    {
        // Serialize the request to JsonObject
        var serializedRequest = JsonSerializer.SerializeToNode(request, _jsonOptions)?.AsObject()
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
                    return await ProcessNewNonStreamingInteraction(request, serializedRequest, cancellationToken);
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
            return JsonSerializer.Deserialize<AnthropicResponse>(responseJson, _jsonOptions)!;
        }

        return await ProcessNewNonStreamingInteraction(request, serializedRequest, cancellationToken);
    }

    /// <summary>
    /// Processes a new non-streaming interaction when no test data exists or when adding additional interactions.
    /// </summary>
    private async Task<AnthropicResponse> ProcessNewNonStreamingInteraction(
        AnthropicRequest request,
        JsonObject serializedRequest,
        CancellationToken cancellationToken)
    {
        // Call the inner client directly and let any exceptions propagate
        AnthropicResponse response = await _innerClient.CreateChatCompletionsAsync(
            request, cancellationToken);

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
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of stream events.</returns>
    public async IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
        AnthropicRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Serialize the request to JsonObject
        var serializedRequest = JsonSerializer.SerializeToNode(request, _jsonOptions)?.AsObject()
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
                    await foreach (var response in ProcessNewStreamingInteractionAsync(request, serializedRequest, cancellationToken))
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
                yield return JsonSerializer.Deserialize<AnthropicStreamEvent>(fragmentJson.ToJsonString(), _jsonOptions)!;
            }

            yield break;
        }

        await foreach (var response in ProcessNewStreamingInteractionAsync(request, serializedRequest, cancellationToken))
        {
            yield return response;
        }
    }

    /// <summary>
    /// Processes a new streaming interaction when no test data exists or when adding additional interactions.
    /// </summary>
    private async IAsyncEnumerable<AnthropicStreamEvent> ProcessNewStreamingInteractionAsync(
        AnthropicRequest request,
        JsonObject serializedRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Get responses from inner client
        var responseFragments = new List<JsonObject>();

        await foreach (AnthropicStreamEvent streamEvent in _innerClient.StreamingChatCompletionsAsync(
            request, cancellationToken))
        {
            // Save the fragment
            var serializedFragment = JsonSerializer.SerializeToNode(streamEvent, _jsonOptions)?.AsObject()
                ?? throw new InvalidOperationException("Failed to serialize stream event to JsonObject");
            responseFragments.Add(serializedFragment);
            
            // Return the event to the caller
            yield return streamEvent;
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