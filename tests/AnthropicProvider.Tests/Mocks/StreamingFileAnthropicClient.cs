using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Mocks;

/// <summary>
/// A mock Anthropic client that reads SSE events from a file and returns them as a stream
/// </summary>
public class StreamingFileAnthropicClient : IAnthropicClient
{
  private readonly string _filePath;
  private readonly JsonSerializerOptions _jsonOptions;

  /// <summary>
  /// Initializes a new instance of the <see cref="StreamingFileAnthropicClient"/> class.
  /// </summary>
  /// <param name="filePath">Path to the file containing SSE events</param>
  public StreamingFileAnthropicClient(string filePath)
  {
    _filePath = filePath;
    _jsonOptions = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true
    };
  }

  /// <inheritdoc/>
  public Task<AnthropicResponse> CreateChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    throw new NotImplementedException("Only streaming mode is supported by this mock client");
  }

  /// <inheritdoc/>
  public Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
    AnthropicRequest request,
    CancellationToken cancellationToken = default)
  {
    return Task.FromResult<IAsyncEnumerable<AnthropicStreamEvent>>(
      new FileStreamEventEnumerable(_filePath, _jsonOptions, cancellationToken));
  }

  private class FileStreamEventEnumerable : IAsyncEnumerable<AnthropicStreamEvent>
  {
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationToken _cancellationToken;

    public FileStreamEventEnumerable(
      string filePath,
      JsonSerializerOptions jsonOptions,
      CancellationToken cancellationToken)
    {
      _filePath = filePath;
      _jsonOptions = jsonOptions;
      _cancellationToken = cancellationToken;
    }

    public async IAsyncEnumerator<AnthropicStreamEvent> GetAsyncEnumerator(
      CancellationToken cancellationToken = default)
    {
      cancellationToken = _cancellationToken.IsCancellationRequested ? _cancellationToken : cancellationToken;
      
      // Read the file content
      var fileContent = await File.ReadAllTextAsync(_filePath, cancellationToken);
      
      // Parse the file content into SSE events
      var events = ParseSseEvents(fileContent);
      
      foreach (var sseEvent in events)
      {
        // Skip empty data
        if (string.IsNullOrEmpty(sseEvent.Data))
        {
          continue;
        }
        
        // Parse the event data based on the event type
        AnthropicStreamEvent? eventData = null;
        try
        {
          // First try to deserialize as the base AnthropicStreamEvent to get the type
          var baseEvent = JsonSerializer.Deserialize<AnthropicStreamEvent>(sseEvent.Data, _jsonOptions);
          
          if (baseEvent == null)
          {
            continue;
          }
          
          // Then deserialize to the appropriate specialized type based on the event type
          eventData = baseEvent.Type switch
          {
            "message_start" => JsonSerializer.Deserialize<AnthropicMessageStartEvent>(sseEvent.Data, _jsonOptions),
            "content_block_start" => JsonSerializer.Deserialize<AnthropicContentBlockStartEvent>(sseEvent.Data, _jsonOptions),
            "content_block_delta" => JsonSerializer.Deserialize<AnthropicContentBlockDeltaEvent>(sseEvent.Data, _jsonOptions),
            "content_block_stop" => JsonSerializer.Deserialize<AnthropicContentBlockStopEvent>(sseEvent.Data, _jsonOptions),
            "message_delta" => JsonSerializer.Deserialize<AnthropicMessageDeltaEvent>(sseEvent.Data, _jsonOptions),
            "message_stop" => JsonSerializer.Deserialize<AnthropicMessageStopEvent>(sseEvent.Data, _jsonOptions),
            "ping" => JsonSerializer.Deserialize<AnthropicPingEvent>(sseEvent.Data, _jsonOptions),
            "error" => JsonSerializer.Deserialize<AnthropicErrorEvent>(sseEvent.Data, _jsonOptions),
            _ => baseEvent // Use the base event if type is unknown
          };
          
          // Add a small delay to simulate streaming
          await Task.Delay(1, cancellationToken);
        }
        catch (JsonException ex)
        {
          // Log exception and continue
          Console.Error.WriteLine($"Error parsing SSE data: {ex.Message}");
          continue;
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
  /// Simple class to represent a server-sent event
  /// </summary>
  private class ServerSentEvent
  {
    /// <summary>
    /// Gets or sets the event type
    /// </summary>
    public string EventType { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets or sets the event data
    /// </summary>
    public string Data { get; set; } = string.Empty;
  }

  /// <summary>
  /// Helper method to parse SSE events from a string
  /// </summary>
  private static List<ServerSentEvent> ParseSseEvents(string content)
  {
    var events = new List<ServerSentEvent>();
    var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    
    ServerSentEvent? currentEvent = null;
    
    foreach (var line in lines)
    {
      if (string.IsNullOrEmpty(line))
      {
        // Empty line indicates the end of an event
        if (currentEvent != null)
        {
          events.Add(currentEvent);
          currentEvent = null;
        }
        continue;
      }
      
      if (line.StartsWith("event: "))
      {
        // New event
        if (currentEvent != null)
        {
          events.Add(currentEvent);
        }
        
        currentEvent = new ServerSentEvent
        {
          EventType = line.Substring(7).Trim()
        };
      }
      else if (line.StartsWith("data: ") && currentEvent != null)
      {
        // Data for the current event
        currentEvent.Data = line.Substring(6).Trim();
      }
    }
    
    // Add the last event if there is one
    if (currentEvent != null)
    {
      events.Add(currentEvent);
    }
    
    return events;
  }

  /// <inheritdoc/>
  public void Dispose()
  {
    // Nothing to dispose
  }
} 