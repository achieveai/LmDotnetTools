using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;

/// <summary>
/// Client for interacting with the Anthropic API.
/// </summary>
public class AnthropicClient : IAnthropicClient
{
  private readonly HttpClient _httpClient;
  private readonly string _apiKey;
  private const string BaseUrl = "https://api.anthropic.com/v1";
  private readonly JsonSerializerOptions _jsonOptions;
  private bool _disposed = false;

  /// <summary>
  /// Initializes a new instance of the <see cref="AnthropicClient"/> class.
  /// </summary>
  /// <param name="apiKey">The API key to use for authentication.</param>
  /// <param name="httpClient">Optional custom HTTP client to use.</param>
  public AnthropicClient(string apiKey, HttpClient? httpClient = null)
  {
    _apiKey = apiKey;
    _httpClient = httpClient ?? new HttpClient();
    _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

    _jsonOptions = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
  }

  /// <inheritdoc/>
  public async Task<AnthropicResponse> CreateChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
    
    var response = await _httpClient.PostAsync($"{BaseUrl}/messages", content, cancellationToken);
    response.EnsureSuccessStatusCode();

    var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
    return JsonSerializer.Deserialize<AnthropicResponse>(responseContent, _jsonOptions) 
      ?? throw new InvalidOperationException("Failed to deserialize Anthropic API response");
  }

  /// <inheritdoc/>
  public async IAsyncEnumerable<AnthropicStreamEvent> StreamingChatCompletionsAsync(
    AnthropicRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    // Set the streaming flag
    request = request with { Stream = true };
    
    var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
    
    var request_message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages");
    request_message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request_message.Content = content;
    
    var response = await _httpClient.SendAsync(
      request_message, 
      HttpCompletionOption.ResponseHeadersRead,
      cancellationToken);
    
    response.EnsureSuccessStatusCode();
    
    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var reader = new StreamReader(stream);
    
    while (!reader.EndOfStream)
    {
      cancellationToken.ThrowIfCancellationRequested();
      
      var line = await reader.ReadLineAsync();
      if (string.IsNullOrEmpty(line))
      {
        continue;
      }
      
      // SSE format: lines starting with "data: "
      if (line.StartsWith("data: "))
      {
        var data = line.Substring(6);
        
        // The stream is finished when we receive "[DONE]"
        if (data == "[DONE]")
        {
          break;
        }
        
        // Parse the event data outside the try/catch so we can yield return safely
        AnthropicStreamEvent? eventData = null;
        try
        {
          eventData = JsonSerializer.Deserialize<AnthropicStreamEvent>(data, _jsonOptions);
        }
        catch (JsonException ex)
        {
          // Log exception and continue
          Console.Error.WriteLine($"Error parsing SSE data: {ex.Message}");
        }
        
        // Return the event data if it was successfully parsed
        if (eventData != null)
        {
          yield return eventData;
        }
      }
    }
  }

  /// <summary>
  /// Disposes the HTTP client.
  /// </summary>
  public void Dispose()
  {
    Dispose(true);
    GC.SuppressFinalize(this);
  }

  /// <summary>
  /// Disposes the HTTP client.
  /// </summary>
  /// <param name="disposing">Whether to dispose managed resources.</param>
  protected virtual void Dispose(bool disposing)
  {
    if (!_disposed)
    {
      if (disposing)
      {
        _httpClient.Dispose();
      }
      
      _disposed = true;
    }
  }
}
