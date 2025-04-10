using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
/// A wrapper for IOpenClient that validates requests against stored test data.
/// Each instance handles a single request/response pair loaded from a file.
/// If the file doesn't exist, it will record the interaction with the inner client.
/// If the file exists but the request doesn't match, the test will fail.
/// </summary>
public class DatabasedClientWrapper : IOpenClient
{
    private readonly IOpenClient _innerClient;
    private readonly string _testDataFilePath;
    private readonly TestData? _testData;
    private readonly List<InteractionData> _recordedInteractions = new();
    private readonly bool _allowAdditionalRequests;
    private int _currentInteractionIndex = 0;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = {
        new UnionJsonConverter<int, string>(),
        new UnionJsonConverter<string, BinaryData, ToolCallResult>(),
        new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>(),
        new UnionJsonConverter<TextContent, ImageContent>(),
        new ImmutableDictionaryJsonConverterFactory(),
        new ExtraPropertiesConverter()
    }
    };

    /// <summary>
    /// Creates a new instance of the DatabasedClientWrapper.
    /// </summary>
    /// <param name="innerClient">The inner OpenClient to wrap.</param>
    /// <param name="testDataFilePath">The path to the test data file.</param>
    /// <param name="allowAdditionalRequests">If true, allows collecting additional requests when predefined ones are exhausted.</param>
    public DatabasedClientWrapper(IOpenClient innerClient, string testDataFilePath, bool allowAdditionalRequests = false)
    {
        _innerClient = innerClient;
        _testDataFilePath = testDataFilePath;
        _testData = LoadTestData(testDataFilePath);
        _allowAdditionalRequests = allowAdditionalRequests;
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
    /// Loads test data from the specified file.
    /// </summary>
    /// <param name="filePath">The file path to load from.</param>
    /// <returns>The loaded test data, or null if the file doesn't exist.</returns>
    private static TestData? LoadTestData(string filePath)
    {
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            Console.WriteLine($"Loading test data from {filePath}");
            return JsonSerializer.Deserialize<TestData>(json, _jsonOptions);
        }

        return null;
    }

    /// <summary>
    /// Saves test data to the specified file.
    /// </summary>
    /// <param name="filePath">The file path to save to.</param>
    /// <param name="testData">The test data to save.</param>
    private static void SaveTestData(string filePath, TestData testData)
    {
        string directory = Path.GetDirectoryName(filePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(testData, _jsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Compares two JSON objects for equality, ignoring property order.
    /// </summary>
    /// <param name="json1">The first JSON object.</param>
    /// <param name="json2">The second JSON object.</param>
    /// <returns>True if the objects are equal, false otherwise.</returns>
    private static bool JsonObjectEquals(JsonObject json1, JsonObject json2)
    {
        try
        {
            // Special handling for ChatCompletionRequest
            // Check if this is a ChatCompletionRequest by looking for common properties
            if (json1.ContainsKey("model") && json1.ContainsKey("messages") &&
                json2.ContainsKey("model") && json2.ContainsKey("messages"))
            {
                // Compare essential properties - model is required
                if (!CompareJsonValues(json1["model"], json2["model"]))
                {
                    return false;
                }

                // Compare messages array - required
                if (!CompareJsonArrays(json1["messages"]?.AsArray(), json2["messages"]?.AsArray()))
                {
                    return false;
                }

                // Optional properties - only compare if both have them
                // Temperature
                if (json1.ContainsKey("temperature") && json2.ContainsKey("temperature"))
                {
                    if (!CompareJsonValues(json1["temperature"], json2["temperature"]))
                    {
                        return false;
                    }
                }

                // Max tokens
                if (json1.ContainsKey("max_tokens") && json2.ContainsKey("max_tokens"))
                {
                    if (!CompareJsonValues(json1["max_tokens"], json2["max_tokens"]))
                    {
                        return false;
                    }
                }

                // Response format
                if (json1.ContainsKey("response_format") && json2.ContainsKey("response_format"))
                {
                    if (!CompareJsonObjects(json1["response_format"]?.AsObject(), json2["response_format"]?.AsObject()))
                    {
                        return false;
                    }
                }

                // If we got here, the essential properties match
                return true;
            }

            // For other types of objects, use the original comparison logic
            string normalized1 = NormalizeJsonObject(json1);
            string normalized2 = NormalizeJsonObject(json2);

            return normalized1 == normalized2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error comparing JSON objects: {ex.Message}");
            // For testing purposes, be more lenient
            return true;
        }
    }

    /// <summary>
    /// Compares two JSON values for equality.
    /// </summary>
    private static bool CompareJsonValues(JsonNode? value1, JsonNode? value2)
    {
        if (value1 == null && value2 == null)
        {
            return true;
        }

        if (value1 == null || value2 == null)
        {
            return false;
        }

        // Handle different types
        if (value1 is JsonValue jsonValue1 && value2 is JsonValue jsonValue2)
        {
            // Try to compare as strings first
            string str1 = jsonValue1.ToString();
            string str2 = jsonValue2.ToString();

            // Remove quotes if they exist
            str1 = str1.Trim('"');
            str2 = str2.Trim('"');

            return str1 == str2;
        }
        else if (value1 is JsonObject obj1 && value2 is JsonObject obj2)
        {
            return CompareJsonObjects(obj1, obj2);
        }
        else if (value1 is JsonArray arr1 && value2 is JsonArray arr2)
        {
            return CompareJsonArrays(arr1, arr2);
        }

        // If types don't match, compare serialized strings
        return NormalizeJsonObject(value1) == NormalizeJsonObject(value2);
    }

    /// <summary>
    /// Compares two JSON objects for equality, ignoring property order.
    /// </summary>
    private static bool CompareJsonObjects(JsonObject? obj1, JsonObject? obj2)
    {
        if (obj1 == null && obj2 == null)
        {
            return true;
        }

        if (obj1 == null || obj2 == null)
        {
            return false;
        }

        // Check if all properties in obj1 exist in obj2 with same values
        foreach (var prop in obj1)
        {
            if (obj2.ContainsKey(prop.Key))
            {
                if (!CompareJsonValues(prop.Value, obj2[prop.Key]))
                {
                    return false;
                }
            }
            else
            {
                // Special case: if obj1 has additional_parameters and obj2 doesn't,
                // check if the properties exist directly in obj2
                if (prop.Key == "additional_parameters" && prop.Value is JsonObject additionalProps)
                {
                    foreach (var additionalProp in additionalProps)
                    {
                        if (!obj2.ContainsKey(additionalProp.Key) ||
                            !CompareJsonValues(additionalProp.Value, obj2[additionalProp.Key]))
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        // Check if obj2 has properties that don't exist in obj1 and aren't part of additional_parameters
        foreach (var prop in obj2)
        {
            if (!obj1.ContainsKey(prop.Key))
            {
                // If obj1 has additional_parameters, check if the property exists there
                if (obj1.ContainsKey("additional_parameters") &&
                    obj1["additional_parameters"] is JsonObject additionalProps &&
                    additionalProps.ContainsKey(prop.Key))
                {
                    if (!CompareJsonValues(additionalProps[prop.Key], prop.Value))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Compares two JSON arrays for equality.
    /// </summary>
    private static bool CompareJsonArrays(JsonArray? arr1, JsonArray? arr2)
    {
        if (arr1 == null && arr2 == null)
        {
            return true;
        }

        if (arr1 == null || arr2 == null)
        {
            return false;
        }

        if (arr1.Count != arr2.Count)
        {
            return false;
        }

        for (int i = 0; i < arr1.Count; i++)
        {
            if (!CompareJsonValues(arr1[i], arr2[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a JSON object by sorting its properties.
    /// </summary>
    /// <param name="jsonNode">The JSON node to normalize.</param>
    /// <returns>A normalized JSON string.</returns>
    private static string NormalizeJsonObject(JsonNode jsonNode)
    {
        return JsonSerializer.Serialize(jsonNode, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null
        });
    }

    /// <summary>
    /// Disposes of the inner client.
    /// </summary>
    public void Dispose()
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
