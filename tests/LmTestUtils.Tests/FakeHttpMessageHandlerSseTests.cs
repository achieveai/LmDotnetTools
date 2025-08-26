using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Xunit;

namespace LmTestUtils.Tests;

public class FakeHttpMessageHandlerSseTests
{
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
        System.Diagnostics.Debug.WriteLine($"SSE Content: {content}");

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
        System.Diagnostics.Debug.WriteLine($"Simple SSE Content: {content}");

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
        System.Diagnostics.Debug.WriteLine($"JSON SSE Content: {content}");

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
        System.Diagnostics.Debug.WriteLine($"Multiline SSE Content: {content}");

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
        System.Diagnostics.Debug.WriteLine($"No Event Type SSE Content: {content}");

        // Verify no event field when not specified
        Assert.DoesNotContain("event:", content);
        Assert.Contains("data: Simple message", content);
        Assert.Contains("id: 1", content);
    }
}
