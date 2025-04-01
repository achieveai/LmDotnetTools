using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Mocks;

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
  private static readonly JsonSerializerOptions _jsonOptions = new()
  {
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    Converters = {
        new UnionJsonConverter<int, string>(),
        new UnionJsonConverter<string, BinaryData, ToolCallResult>(),
        new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>(),
        new UnionJsonConverter<TextContent, ImageContent>(),
    }
  };

  /// <summary>
  /// Creates a new instance of the DatabasedClientWrapper.
  /// </summary>
  /// <param name="innerClient">The inner OpenClient to wrap.</param>
  /// <param name="testDataFilePath">The path to the test data file.</param>
  public DatabasedClientWrapper(IOpenClient innerClient, string testDataFilePath)
  {
    _innerClient = innerClient;
    _testDataFilePath = testDataFilePath;
    _testData = LoadTestData(testDataFilePath);
  }

  /// <summary>
  /// Creates a chat completion by either returning cached data or recording a new interaction.
  /// </summary>
  /// <param name="chatCompletionRequest">The request to process.</param>
  /// <param name="cancellationToken">A cancellation token.</param>
  /// <returns>The chat completion response.</returns>
  public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
    ChatCompletionRequest chatCompletionRequest,
    CancellationToken cancellationToken = default)
  {
    // Serialize the request
    string serializedRequest = JsonSerializer.Serialize(chatCompletionRequest, _jsonOptions);
    
    // If we have test data and it's not a streaming request
    if (_testData != null && !_testData.IsStreaming)
    {
      // Validate that the request matches
      if (!JsonObjectEquals(_testData.SerializedRequest, serializedRequest))
      {
        throw new InvalidOperationException("The request does not match the expected test data request.");
      }
      
      // Return the stored response
      return JsonSerializer.Deserialize<ChatCompletionResponse>(_testData.SerializedResponse, _jsonOptions)!;
    }
    
    // No test data exists, so record a new interaction
    ChatCompletionResponse response = await _innerClient.CreateChatCompletionsAsync(
      chatCompletionRequest, cancellationToken);
      
    // Save the interaction to the file
    string serializedResponse = JsonSerializer.Serialize(response, _jsonOptions);
    SaveTestData(_testDataFilePath, new TestData
    {
      SerializedRequest = serializedRequest,
      SerializedResponse = serializedResponse,
      IsStreaming = false
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
    // Serialize the request
    string serializedRequest = JsonSerializer.Serialize(chatCompletionRequest, _jsonOptions);
    
    // If we have test data and it's a streaming request
    if (_testData != null && _testData.IsStreaming)
    {
      // Validate that the request matches
      if (!JsonObjectEquals(_testData.SerializedRequest, serializedRequest))
      {
        throw new InvalidOperationException("The request does not match the expected test data request.");
      }
      
      // Return each fragment with a small delay between them
      foreach (string fragmentJson in _testData.SerializedResponseFragments)
      {
        // Add a small delay between fragments to simulate streaming
        await Task.Delay(1, cancellationToken);
        yield return JsonSerializer.Deserialize<ChatCompletionResponse>(fragmentJson, _jsonOptions)!;
      }
      
      yield break;
    }
    
    // No test data exists, so record a new interaction
    List<string> responseFragments = new();
    
    await foreach (ChatCompletionResponse response in _innerClient.StreamingChatCompletionsAsync(
      chatCompletionRequest, cancellationToken))
    {
      // Save the fragment
      string serializedFragment = JsonSerializer.Serialize(response, _jsonOptions);
      responseFragments.Add(serializedFragment);
      
      // Return the response to the caller
      yield return response;
    }
    
    // Save the interaction to the file
    SaveTestData(_testDataFilePath, new TestData
    {
      SerializedRequest = serializedRequest,
      SerializedResponseFragments = responseFragments,
      IsStreaming = true
    });
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
  /// <param name="json1">The first JSON string.</param>
  /// <param name="json2">The second JSON string.</param>
  /// <returns>True if the objects are equal, false otherwise.</returns>
  private static bool JsonObjectEquals(string json1, string json2)
  {
    try
    {
      // Parse the JSON strings into JsonNode objects
      JsonNode? obj1 = JsonNode.Parse(json1);
      JsonNode? obj2 = JsonNode.Parse(json2);
      
      if (obj1 == null || obj2 == null)
      {
        return false;
      }
      
      // Convert to string with sorted properties for comparison
      string normalized1 = NormalizeJsonObject(obj1);
      string normalized2 = NormalizeJsonObject(obj2);
      
      return normalized1 == normalized2;
    }
    catch
    {
      return false;
    }
  }

  /// <summary>
  /// Normalizes a JSON object by sorting its properties.
  /// </summary>
  /// <param name="node">The JsonNode to normalize.</param>
  /// <returns>A normalized JSON string.</returns>
  private static string NormalizeJsonObject(JsonNode node)
  {
    return node switch
    {
      JsonObject obj => NormalizeJsonObject(obj),
      JsonArray arr => NormalizeJsonArray(arr),
      _ => node.ToJsonString()
    };
  }
  
  private static string NormalizeJsonObject(JsonObject obj)
  {
    var result = new JsonObject();
    
    // Get all properties and sort them by name
    var sortedProperties = obj.Select(kvp => kvp.Key).OrderBy(k => k).ToList();
    
    // Add each property with normalized values
    foreach (string key in sortedProperties)
    {
      if (obj[key] is JsonObject childObj)
      {
        result.Add(key, JsonNode.Parse(NormalizeJsonObject(childObj)));
      }
      else if (obj[key] is JsonArray childArr)
      {
        result.Add(key, JsonNode.Parse(NormalizeJsonArray(childArr)));
      }
      else
      {
        result.Add(key, obj[key]?.DeepClone());
      }
    }
    
    return result.ToJsonString();
  }
  
  private static string NormalizeJsonArray(JsonArray arr)
  {
    var result = new JsonArray();
    
    foreach (JsonNode? item in arr)
    {
      if (item is JsonObject childObj)
      {
        result.Add(JsonNode.Parse(NormalizeJsonObject(childObj)));
      }
      else if (item is JsonArray childArr)
      {
        result.Add(JsonNode.Parse(NormalizeJsonArray(childArr)));
      }
      else
      {
        result.Add(item?.DeepClone());
      }
    }
    
    return result.ToJsonString();
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
/// Represents test data for a single request/response interaction.
/// </summary>
public record TestData
{
  /// <summary>
  /// Gets or sets the serialized request.
  /// </summary>
  public string SerializedRequest { get; init; } = string.Empty;
  
  /// <summary>
  /// Gets or sets the serialized response for non-streaming requests.
  /// </summary>
  public string SerializedResponse { get; init; } = string.Empty;
  
  /// <summary>
  /// Gets or sets the serialized response fragments for streaming requests.
  /// </summary>
  public List<string> SerializedResponseFragments { get; init; } = new();
  
  /// <summary>
  /// Gets or sets a value indicating whether this is a streaming interaction.
  /// </summary>
  public bool IsStreaming { get; init; }
}