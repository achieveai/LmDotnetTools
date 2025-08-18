using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class TodoContextMiddlewareTests
{
  [Fact]
  public void EnrichWithTodoContext_WithActiveTasks_AddsTodoContextMessage()
  {
    // Arrange
    var todoContext = "Current Tasks:\n[ ] Task 1: Test something\n[-] Task 2: In progress";
    var middleware = new TodoContextMiddleware(() => todoContext);

    var originalMessages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User }
        };

    // Act
    var enrichedMessages = middleware.GetType()
        .GetMethod("EnrichWithTodoContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.Invoke(middleware, new object[] { originalMessages }) as IEnumerable<IMessage>;

    // Assert
    Assert.NotNull(enrichedMessages);
    var messageList = enrichedMessages.ToList();
    Assert.Equal(2, messageList.Count);
    Assert.IsType<TextMessage>(messageList[0]);
    Assert.IsType<TodoContextMessage>(messageList[1]);

    var todoMessage = (TodoContextMessage)messageList[1];
    Assert.Equal(todoContext, todoMessage.TodoContext);
    Assert.Equal(Role.System, todoMessage.Role);
    Assert.Equal("TodoContextMiddleware", todoMessage.FromAgent);
  }

  [Fact]
  public void EnrichWithTodoContext_WithEmptyContext_DoesNotAddTodoMessage()
  {
    // Arrange
    var middleware = new TodoContextMiddleware(() => "");

    var originalMessages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User }
        };

    // Act
    var enrichedMessages = middleware.GetType()
        .GetMethod("EnrichWithTodoContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.Invoke(middleware, new object[] { originalMessages }) as IEnumerable<IMessage>;

    // Assert
    Assert.NotNull(enrichedMessages);
    var messageList = enrichedMessages.ToList();
    Assert.Single(messageList);
    Assert.IsType<TextMessage>(messageList[0]);
  }

  [Fact]
  public void EnrichWithTodoContext_WithNullContext_DoesNotAddTodoMessage()
  {
    // Arrange
    var middleware = new TodoContextMiddleware(() => null!);

    var originalMessages = new List<IMessage>
        {
            new TextMessage { Text = "Hello", Role = Role.User }
        };

    // Act
    var enrichedMessages = middleware.GetType()
        .GetMethod("EnrichWithTodoContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?.Invoke(middleware, new object[] { originalMessages }) as IEnumerable<IMessage>;

    // Assert
    Assert.NotNull(enrichedMessages);
    var messageList = enrichedMessages.ToList();
    Assert.Single(messageList);
    Assert.IsType<TextMessage>(messageList[0]);
  }
}
