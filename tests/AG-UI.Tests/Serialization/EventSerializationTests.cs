using System;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;
using Xunit;

namespace AchieveAi.LmDotnetTools.AgUi.Tests.Serialization;

public class EventSerializationTests
{
    [Fact]
    public void SessionStartedEvent_Serialization_Succeeds()
    {
        // Arrange
        var sessionStartedEvent = new SessionStartedEvent
        {
            SessionId = "test-session-123",
            StartedAt = DateTime.UtcNow
        };

        // Act & Assert - should not throw
        var json = JsonSerializer.Serialize<AgUiEventBase>(sessionStartedEvent, AgUiJsonOptions.Default);

        // Verify JSON contains type discriminator
        Assert.Contains("\"type\"", json);
        Assert.Contains("SESSION_STARTED", json);
    }

    [Fact]
    public void SessionStartedEvent_RoundTrip_PreservesData()
    {
        // Arrange
        var original = new SessionStartedEvent
        {
            SessionId = "test-session-456",
            StartedAt = new DateTime(2025, 11, 16, 12, 0, 0, DateTimeKind.Utc)
        };

        // Act
        var json = JsonSerializer.Serialize<AgUiEventBase>(original, AgUiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<AgUiEventBase>(json, AgUiJsonOptions.Default);

        // Assert
        Assert.NotNull(deserialized);
        Assert.IsType<SessionStartedEvent>(deserialized);

        var sessionEvent = (SessionStartedEvent)deserialized;
        Assert.Equal("SESSION_STARTED", sessionEvent.Type);
        Assert.Equal(original.SessionId, sessionEvent.SessionId);
    }

    [Fact]
    public void RunStartedEvent_Serialization_IncludesTypeDiscriminator()
    {
        // Arrange
        var runStartedEvent = new RunStartedEvent
        {
            SessionId = "session-123"
        };

        // Act
        var json = JsonSerializer.Serialize<AgUiEventBase>(runStartedEvent, AgUiJsonOptions.Default);

        // Assert
        Assert.Contains("\"type\":\"RUN_STARTED\"", json);
    }

    [Fact]
    public void ErrorEvent_Serialization_WorksWithPolymorphism()
    {
        // Arrange
        var errorEvent = new ErrorEvent
        {
            SessionId = "session-789",
            ErrorCode = "TEST_ERROR",
            Message = "Test error message",
            Recoverable = true
        };

        // Act
        var json = JsonSerializer.Serialize<AgUiEventBase>(errorEvent, AgUiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<AgUiEventBase>(json, AgUiJsonOptions.Default);

        // Assert
        Assert.NotNull(deserialized);
        Assert.IsType<ErrorEvent>(deserialized);

        var errorEventDeserialized = (ErrorEvent)deserialized;
        Assert.Equal("RUN_ERROR", errorEventDeserialized.Type);
        Assert.Equal(errorEvent.ErrorCode, errorEventDeserialized.ErrorCode);
        Assert.Equal(errorEvent.Message, errorEventDeserialized.Message);
        Assert.Equal(errorEvent.Recoverable, errorEventDeserialized.Recoverable);
    }

    [Fact]
    public void TextMessageContentEvent_Serialization_PreservesContent()
    {
        // Arrange
        var textEvent = new TextMessageContentEvent
        {
            SessionId = "session-abc",
            Content = "Hello, World!"
        };

        // Act
        var json = JsonSerializer.Serialize<AgUiEventBase>(textEvent, AgUiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<AgUiEventBase>(json, AgUiJsonOptions.Default);

        // Assert
        Assert.NotNull(deserialized);
        Assert.IsType<TextMessageContentEvent>(deserialized);

        var textEventDeserialized = (TextMessageContentEvent)deserialized;
        Assert.Equal("TEXT_MESSAGE_CONTENT", textEventDeserialized.Type);
        Assert.Equal(textEvent.Content, textEventDeserialized.Content);
    }
}
