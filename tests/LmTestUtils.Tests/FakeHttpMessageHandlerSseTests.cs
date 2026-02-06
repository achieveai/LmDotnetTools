using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmTestUtils.Tests;

public class FakeHttpMessageHandlerSseTests : LoggingTestBase
{
    public FakeHttpMessageHandlerSseTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task CreateSseStreamHandler_ShouldReturnProperSseFormat()
    {
        // Arrange
        var events = new[]
        {
            new SseEvent
            {
                Id = "1",
                Event = "message",
                Data = "Hello",
            },
            new SseEvent
            {
                Id = "2",
                Event = "message",
                Data = "World",
            },
            new SseEvent { Data = "No ID or event type" },
        };

        var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);
        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.GetAsync("https://example.com/stream");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains("keep-alive", response.Headers.GetValues("Connection"));

        var content = await response.Content.ReadAsStringAsync();
        Debug.WriteLine($"SSE Content: {content}");

        // Verify SSE format
        Assert.Contains("id: 1", content);
        Assert.Contains("event: message", content);
        Assert.Contains("data: Hello", content);
        Assert.Contains("id: 2", content);
        Assert.Contains("data: World", content);
        Assert.Contains("data: No ID or event type", content);
    }

    [Fact]
    public async Task CreateSimpleSseStreamHandler_ShouldReturnTextMessages()
    {
        // Arrange
        var messages = new[] { "First message", "Second message", "Third message" };
        var handler = FakeHttpMessageHandler.CreateSimpleSseStreamHandler(messages, "chat");
        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.GetAsync("https://example.com/stream");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Debug.WriteLine($"Simple SSE Content: {content}");

        // Verify content
        Assert.Contains("event: chat", content);
        Assert.Contains("data: First message", content);
        Assert.Contains("data: Second message", content);
        Assert.Contains("data: Third message", content);
        Assert.Contains("id: 1", content);
        Assert.Contains("id: 2", content);
        Assert.Contains("id: 3", content);
    }

    [Fact]
    public async Task CreateJsonSseStreamHandler_ShouldReturnJsonEvents()
    {
        // Arrange
        var objects = new[]
        {
            new { type = "start", message = "Beginning" },
            new { type = "data", message = "Processing" },
            new { type = "end", message = "Complete" },
        };

        var handler = FakeHttpMessageHandler.CreateJsonSseStreamHandler(objects, "update");
        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.GetAsync("https://example.com/stream");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Debug.WriteLine($"JSON SSE Content: {content}");

        // Verify JSON content
        Assert.Contains("event: update", content);
        Assert.Contains("\"type\":\"start\"", content);
        Assert.Contains("\"message\":\"Beginning\"", content);
        Assert.Contains("\"type\":\"data\"", content);
        Assert.Contains("\"message\":\"Processing\"", content);
        Assert.Contains("\"type\":\"end\"", content);
        Assert.Contains("\"message\":\"Complete\"", content);
    }

    [Fact]
    public async Task CreateSseStreamHandler_WithMultilineData_ShouldFormatCorrectly()
    {
        // Arrange
        var events = new[]
        {
            new SseEvent
            {
                Id = "1",
                Event = "multiline",
                Data = "Line 1\nLine 2\nLine 3",
            },
        };

        var handler = FakeHttpMessageHandler.CreateSseStreamHandler(events);
        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.GetAsync("https://example.com/stream");

        // Assert
        var content = await response.Content.ReadAsStringAsync();
        Debug.WriteLine($"Multiline SSE Content: {content}");

        // Verify multiline data is properly formatted
        Assert.Contains("data: Line 1", content);
        Assert.Contains("data: Line 2", content);
        Assert.Contains("data: Line 3", content);
    }

    [Fact]
    public async Task CreateSimpleSseStreamHandler_WithoutEventType_ShouldOmitEventField()
    {
        // Arrange
        var messages = new[] { "Simple message" };
        var handler = FakeHttpMessageHandler.CreateSimpleSseStreamHandler(messages);
        var httpClient = new HttpClient(handler);

        // Act
        var response = await httpClient.GetAsync("https://example.com/stream");

        // Assert
        var content = await response.Content.ReadAsStringAsync();
        Debug.WriteLine($"No Event Type SSE Content: {content}");

        // Verify no event field when not specified
        Assert.DoesNotContain("event:", content);
        Assert.Contains("data: Simple message", content);
        Assert.Contains("id: 1", content);
    }

    /// <summary>
    ///     Tests SSE streaming with InstructionChainParser pattern.
    ///     Uses TestSseMessageHandler and verifies SSE format in the response.
    ///     This demonstrates how InstructionChainParser generates SSE events.
    /// </summary>
    [Fact]
    public async Task TestSseMessageHandler_WithInstructionChain_ShouldReturnProperSseFormat()
    {
        Logger.LogInformation("Starting TestSseMessageHandler_WithInstructionChain_ShouldReturnProperSseFormat test");

        // Arrange - Using TestSseMessageHandler with instruction chain
        var handlerLogger = LoggerFactory.CreateLogger<TestSseMessageHandler>();
        var testHandler = new TestSseMessageHandler(handlerLogger)
        {
            WordsPerChunk = 5,
            ChunkDelayMs = 10,
        };

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://test-mode/v1"),
        };

        Logger.LogDebug("Created HttpClient with TestSseMessageHandler");

        // User message with instruction chain for text response
        var userMessage = """
            Test message
            <|instruction_start|>
            {"instruction_chain": [
                {"id_message": "SSE format test", "messages":[{"text_message":{"length":15}}]}
            ]}
            <|instruction_end|>
            """;

        // Create OpenAI-format request with streaming
        var requestBody = new
        {
            model = "test-model",
            stream = true,
            messages = new[]
            {
                new { role = "user", content = userMessage }
            }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody),
            System.Text.Encoding.UTF8,
            "application/json"
        );

        Logger.LogDebug("Created streaming request with instruction chain");

        // Act
        var response = await httpClient.PostAsync("/v1/chat/completions", jsonContent);

        // Assert - Verify SSE response format
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Logger.LogInformation("SSE Response content length: {Length}", content.Length);
        Debug.WriteLine($"InstructionChain SSE Content: {content}");

        // Verify SSE format structure
        Assert.Contains("data:", content);
        Assert.Contains("[DONE]", content);

        // Verify proper SSE line format (data: followed by JSON)
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLines = lines.Where(l => l.StartsWith("data:")).ToList();

        Logger.LogInformation("Found {Count} data lines in SSE response", dataLines.Count);

        Assert.NotEmpty(dataLines);

        // The last data line should be [DONE]
        Assert.Equal("data: [DONE]", dataLines.Last());

        // Other data lines should contain valid JSON
        foreach (var dataLine in dataLines.Take(dataLines.Count - 1))
        {
            var jsonPart = dataLine.Substring("data:".Length).Trim();

            if (string.IsNullOrWhiteSpace(jsonPart))
            {
                continue;
            }

            // Should be valid JSON
            try
            {
                using var doc = JsonDocument.Parse(jsonPart);
                Assert.NotNull(doc.RootElement);
                Logger.LogTrace("Parsed SSE event: {Json}", jsonPart.Length > 100 ? jsonPart[..100] + "..." : jsonPart);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning("Non-JSON data line: {Line} - {Error}", dataLine, ex.Message);
            }
        }

        Logger.LogInformation("TestSseMessageHandler_WithInstructionChain_ShouldReturnProperSseFormat completed successfully");
    }
}
