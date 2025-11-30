using AchieveAi.LmDotnetTools.LmCore.Messages;
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
        assignment.InputId.Should().BeNull();
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
            "input-id",
            "parent-run-id",
            WasInjected: true);

        // Assert
        assignment.RunId.Should().Be("run-id");
        assignment.GenerationId.Should().Be("gen-id");
        assignment.InputId.Should().Be("input-id");
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
        var assignment = new RunAssignment("run-123", "gen-456", "input-789", "parent-000");

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
}
