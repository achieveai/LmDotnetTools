using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Tests for message DTOs in the LmMultiTurn.Messages namespace.
/// </summary>
public class MessagesTests
{
    #region UserInput Tests

    [Fact]
    public void UserInput_CanBeCreatedWithMessagesOnly()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };

        // Act
        var userInput = new UserInput(messages);

        // Assert
        userInput.Messages.Should().BeEquivalentTo(messages);
        userInput.InputId.Should().BeNull();
        userInput.ParentRunId.Should().BeNull();
    }

    [Fact]
    public void UserInput_CanBeCreatedWithAllParameters()
    {
        // Arrange
        var messages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User },
        };
        var inputId = "input-123";
        var parentRunId = "run-456";

        // Act
        var userInput = new UserInput(messages, inputId, parentRunId);

        // Assert
        userInput.Messages.Should().BeEquivalentTo(messages);
        userInput.InputId.Should().Be(inputId);
        userInput.ParentRunId.Should().Be(parentRunId);
    }

    #endregion

    #region RunAssignment Tests

    [Fact]
    public void RunAssignment_CanBeCreatedWithRequiredParameters()
    {
        // Arrange & Act
        var assignment = new RunAssignment("run-id", "gen-id");

        // Assert
        assignment.RunId.Should().Be("run-id");
        assignment.GenerationId.Should().Be("gen-id");
        assignment.InputIds.Should().BeNull();
        assignment.ParentRunId.Should().BeNull();
        assignment.WasInjected.Should().BeFalse();
    }

    [Fact]
    public void RunAssignment_CanBeCreatedWithAllParameters()
    {
        // Arrange & Act
        var assignment = new RunAssignment(
            "run-id",
            "gen-id",
            ["input-id"],
            "parent-run-id",
            WasInjected: true);

        // Assert
        assignment.RunId.Should().Be("run-id");
        assignment.GenerationId.Should().Be("gen-id");
        assignment.InputIds.Should().Contain("input-id");
        assignment.ParentRunId.Should().Be("parent-run-id");
        assignment.WasInjected.Should().BeTrue();
    }

    #endregion

    #region RunAssignmentMessage Tests

    [Fact]
    public void RunAssignmentMessage_HasCorrectRole()
    {
        // Arrange
        var assignment = new RunAssignment("run-id", "gen-id");

        // Act
        var message = new RunAssignmentMessage
        {
            Assignment = assignment,
            ThreadId = "thread-1",
        };

        // Assert
        message.Role.Should().Be(Role.System);
    }

    [Fact]
    public void RunAssignmentMessage_ExposesAssignmentProperties()
    {
        // Arrange
        var assignment = new RunAssignment("run-123", "gen-456", ["input-789"], "parent-000");

        // Act
        var message = new RunAssignmentMessage
        {
            Assignment = assignment,
            ThreadId = "thread-1",
        };

        // Assert
        message.RunId.Should().Be("run-123");
        message.GenerationId.Should().Be("gen-456");
        message.ParentRunId.Should().Be("parent-000");
        message.ThreadId.Should().Be("thread-1");
    }

    #endregion

    #region RunCompletedMessage Tests

    [Fact]
    public void RunCompletedMessage_HasCorrectRole()
    {
        // Arrange & Act
        var message = new RunCompletedMessage
        {
            CompletedRunId = "run-id",
        };

        // Assert
        message.Role.Should().Be(Role.System);
    }

    [Fact]
    public void RunCompletedMessage_CanIndicateForking()
    {
        // Arrange & Act
        var message = new RunCompletedMessage
        {
            CompletedRunId = "run-123",
            WasForked = true,
            ForkedToRunId = "run-456",
            ThreadId = "thread-1",
            GenerationId = "gen-789",
        };

        // Assert
        message.CompletedRunId.Should().Be("run-123");
        message.RunId.Should().Be("run-123");
        message.WasForked.Should().BeTrue();
        message.ForkedToRunId.Should().Be("run-456");
        message.ThreadId.Should().Be("thread-1");
        message.GenerationId.Should().Be("gen-789");
    }

    [Fact]
    public void RunCompletedMessage_DefaultsToNotForked()
    {
        // Arrange & Act
        var message = new RunCompletedMessage
        {
            CompletedRunId = "run-id",
        };

        // Assert
        message.WasForked.Should().BeFalse();
        message.ForkedToRunId.Should().BeNull();
    }

    #endregion

    #region QueuedInput binary-compat Tests

    [Fact]
    public void QueuedInput_FourArgCompatCtor_LeavesTriggerNull()
    {
        // Regression: the pre-Trigger 4-arg positional shape (input, receiptId, queuedAt, resume)
        // must still construct, with Trigger defaulting to null.
        var messages = new List<IMessage> { new TextMessage { Text = "hi", Role = Role.User } };
        var input = new UserInput(messages);
        var queuedAt = DateTimeOffset.UtcNow;
        var resume = new ResumeSentinel("run-1", "gen-1");

        var queued = new QueuedInput(input, "receipt-1", queuedAt, resume);

        queued.Input.Should().Be(input);
        queued.ReceiptId.Should().Be("receipt-1");
        queued.QueuedAt.Should().Be(queuedAt);
        queued.Resume.Should().Be(resume);
        queued.Trigger.Should().BeNull();
    }

    [Fact]
    public void QueuedInput_FourValueDeconstruct_MatchesPreTriggerShape()
    {
        // Regression: the pre-Trigger 4-value Deconstruct((input, receiptId, queuedAt, resume) = ...)
        // shape must keep working even though the record now carries a 5th (Trigger) member.
        var messages = new List<IMessage> { new TextMessage { Text = "hi", Role = Role.User } };
        var input = new UserInput(messages);
        var queuedAt = DateTimeOffset.UtcNow;
        var queued = new QueuedInput(input, "receipt-2", queuedAt, Resume: null, Trigger: null);

        var (deconstructedInput, receiptId, deconstructedQueuedAt, resume) = queued;

        deconstructedInput.Should().Be(input);
        receiptId.Should().Be("receipt-2");
        deconstructedQueuedAt.Should().Be(queuedAt);
        resume.Should().BeNull();
    }

    #endregion

    #region StreamRecoveryMessage Tests

    [Fact]
    public void StreamRecoveryReason_SerializesAsSlowConsumer_WithBareProductionOptions()
    {
        // Finding #4: StreamRecoveryReason's wire-format converter must be discoverable from the enum
        // type itself (a [JsonConverter] attribute, mirroring Role's own convention - see Role.cs),
        // not depend on a caller (e.g. ChatWebSocketManager) remembering to register it on its own
        // private JsonSerializerOptions. Bare JsonSerializerOptionsFactory.CreateForProduction() - with
        // NO manual converter added - must already serialize the enum as "slow_consumer".
        var message = new StreamRecoveryMessage("thread-1", "run-1", "gen-1", StreamRecoveryReason.SlowConsumer);

        var json = JsonSerializer.Serialize(message, JsonSerializerOptionsFactory.CreateForProduction());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("reason").GetString().Should().Be("slow_consumer");
    }

    #endregion

    #region Client wire-contract discriminators

    /// <summary>
    /// Serializes through the STATIC type and the options the WebSocket pump uses
    /// (<c>ChatWebSocketManager.PumpMessagesToClientAsync</c> serializes an <see cref="IMessage"/> with
    /// <see cref="JsonSerializerOptionsFactory.CreateForProduction"/>), so the bytes asserted below are
    /// the bytes that reach the browser — not what a differently-typed call would produce.
    /// </summary>
    private static string SerializeAsProductionFrame(IMessage message) =>
        JsonSerializer.Serialize(message, JsonSerializerOptionsFactory.CreateForProduction());

    // Both control frames are routed by the chat client on a RAW SUBSTRING match against the serialized
    // payload (samples/LmStreaming.Sample/ClientApp/src/api/wsClient.ts). Neither type is named in a
    // [JsonDerivedType] attribute on IMessage — LmCore has no reference to LmMultiTurn, so it cannot
    // name them — which means the discriminator actually emitted comes from IMessageJsonConverter's
    // TYPE-NAME fallback (strip "Message", convert to snake_case). Nothing in either type's own source
    // states the string the client depends on, so a plain rename or a tweak to that fallback is a
    // SILENT client outage: the frame still ships and still parses, the client simply stops
    // recognising it — never resyncing after a drop, never discarding an abandoned partial.

    [Fact]
    public void StreamRecoveryMessage_EmitsTheDiscriminatorTheChatClientMatchesOn()
    {
        var json = SerializeAsProductionFrame(
            new StreamRecoveryMessage("thread-1", "run-1", "gen-1", StreamRecoveryReason.SlowConsumer));

        // Matched by wsClient.ts: data.includes('"$type":"stream_recovery"').
        json.Should().Contain("\"$type\":\"stream_recovery\"");
    }

    [Fact]
    public void GenerationAbandonedMessage_EmitsTheDiscriminatorTheChatClientMatchesOn()
    {
        var json = SerializeAsProductionFrame(new GenerationAbandonedMessage("thread-1", "run-1", "gen-1"));

        // Matched by wsClient.ts: data.includes('"$type":"generation_abandoned"').
        json.Should().Contain("\"$type\":\"generation_abandoned\"");
    }

    #endregion
}
