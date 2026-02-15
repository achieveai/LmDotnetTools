using System.Net.WebSockets;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.WebSockets;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmStreaming.AspNetCore.Tests;

public class WebSocketConnectionManagerTests
{
    [Fact]
    public void AddConnection_ShouldStoreConnection()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();
        var mockWebSocket = new Mock<WebSocket>();
        var connectionId = "test-connection-1";

        // Act
        manager.AddConnection(connectionId, mockWebSocket.Object);

        // Assert
        manager.ConnectionCount.Should().Be(1);
        manager.GetConnection(connectionId).Should().Be(mockWebSocket.Object);
    }

    [Fact]
    public void RemoveConnection_ShouldRemoveStoredConnection()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();
        var mockWebSocket = new Mock<WebSocket>();
        var connectionId = "test-connection-1";
        manager.AddConnection(connectionId, mockWebSocket.Object);

        // Act
        var result = manager.RemoveConnection(connectionId);

        // Assert
        result.Should().BeTrue();
        manager.ConnectionCount.Should().Be(0);
        manager.GetConnection(connectionId).Should().BeNull();
    }

    [Fact]
    public void RemoveConnection_ShouldReturnFalse_WhenConnectionDoesNotExist()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();

        // Act
        var result = manager.RemoveConnection("non-existent");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void GetConnection_ShouldReturnNull_WhenConnectionDoesNotExist()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();

        // Act
        var result = manager.GetConnection("non-existent");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetActiveConnections_ShouldReturnAllConnectionIds()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();
        var mockWebSocket1 = new Mock<WebSocket>();
        var mockWebSocket2 = new Mock<WebSocket>();
        manager.AddConnection("conn-1", mockWebSocket1.Object);
        manager.AddConnection("conn-2", mockWebSocket2.Object);

        // Act
        var connections = manager.GetActiveConnections().ToList();

        // Assert
        connections.Should().HaveCount(2);
        connections.Should().Contain("conn-1");
        connections.Should().Contain("conn-2");
    }

    [Fact]
    public void AddConnection_ShouldThrowOnNullConnectionId()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();
        var mockWebSocket = new Mock<WebSocket>();

        // Act & Assert
        var act = () => manager.AddConnection(null!, mockWebSocket.Object);
        _ = act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddConnection_ShouldThrowOnNullWebSocket()
    {
        // Arrange
        var manager = new WebSocketConnectionManager();

        // Act & Assert
        var act = () => manager.AddConnection("test", null!);
        _ = act.Should().Throw<ArgumentNullException>();
    }
}
